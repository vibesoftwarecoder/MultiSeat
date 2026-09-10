using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Interop;
using MultiSeat.Shared;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Accounts;

/// <summary>
/// Manages Windows local accounts used by MultiSeat seats.
/// Uses NetApi32 P/Invoke for account CRUD operations.
/// All MultiSeat-managed accounts are prefixed with "MultiSeatSeat" and
/// added to the Users group.
/// </summary>
public sealed class AccountManager
{
    private readonly ILogger<AccountManager> _logger;
    private readonly ConcurrentDictionary<string, AccountInfo> _managedAccounts = new(StringComparer.OrdinalIgnoreCase);

    // Secure credential store — persisted to disk via DPAPI, loaded on startup
    private readonly ConcurrentDictionary<string, string> _credentials = new(StringComparer.OrdinalIgnoreCase);

    // Persisted account store path — survives service restarts
    private static readonly string StorePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MultiSeat", "accounts.json");

    /// <param name="Scope">
    /// Which DPAPI scope <see cref="EncryptedPassword"/> was written at. Absent in stores written
    /// before the move to SYSTEM scope, and absence is exactly how a legacy entry is recognised.
    ///
    /// This has to be recorded because it cannot be detected: DPAPI takes the scope from the blob
    /// itself and ignores the scope argument passed to Unprotect, so a machine-scoped blob decrypts
    /// perfectly well when CurrentUser is requested. An earlier version of this code inferred
    /// "legacy" from a decryption failure that never happens — the migration would have silently
    /// never run, leaving the store machine-scoped forever. A unit test caught it.
    /// </param>
    private record StoredAccount(string Username, string EncryptedPassword, bool IsManaged,
                                 string? Scope = null);

    /// <summary>Scope tag written by the current format — DPAPI CurrentUser, i.e. SYSTEM.</summary>
    private const string ScopeTagUser = "user";

    private readonly bool _grantAdministrator;

    // Builtin group names are LOCALISED — "Users" is "Usuarios" on a Spanish Windows and
    // "Benutzer" on a German one — but NetLocalGroupAddMembers takes a name, not a SID. So the
    // names are resolved from well-known SIDs at startup. The previous code passed the English
    // literals, which silently did nothing outside an English install: the add fails, a warning is
    // logged, and the account is left out of the group.
    private static readonly string UsersGroup =
        ResolveLocalGroupName(WellKnownSidType.BuiltinUsersSid, Constants.AccountGroup);

    private static readonly string RemoteDesktopUsersGroup =
        ResolveLocalGroupName(WellKnownSidType.BuiltinRemoteDesktopUsersSid, "Remote Desktop Users");

    private static readonly string AdministratorsGroup =
        ResolveLocalGroupName(WellKnownSidType.BuiltinAdministratorsSid, "Administrators");

    public AccountManager(ILogger<AccountManager> logger, IOptions<MultiSeatOptions> options)
    {
        _logger = logger;
        _grantAdministrator = options.Value.GrantSeatAdministrator;
        DiscoverExistingAccounts();
        LoadPersistedAccounts();
    }

    /// <summary>
    /// Local name of a builtin group, resolved from its well-known SID so this works on a
    /// non-English Windows. Falls back to the English literal if translation fails.
    /// </summary>
    internal static string ResolveLocalGroupName(WellKnownSidType wellKnown, string fallback)
    {
        try
        {
            var name = new SecurityIdentifier(wellKnown, null)
                .Translate(typeof(NTAccount)).Value;

            // Comes back as "BUILTIN\Users"; the Net* APIs want the bare group name.
            var slash = name.IndexOf('\\');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }
        catch
        {
            return fallback;
        }
    }

    public IReadOnlyCollection<AccountInfo> ListManagedAccounts() =>
        _managedAccounts.Values.ToList().AsReadOnly();

    public bool AccountExists(string username) =>
        _managedAccounts.ContainsKey(username);

    /// <summary>
    /// Retrieve stored password for session creation. Returns null if not stored.
    /// </summary>
    public string? GetCredential(string username) =>
        _credentials.GetValueOrDefault(username);

    /// <summary>
    /// Create a new Windows local account for a MultiSeat seat.
    /// </summary>
    public AccountInfo CreateAccount(string username, string? password = null)
    {
        if (_managedAccounts.ContainsKey(username))
            throw new ResourceConflictException($"Account '{username}' already exists.");

        // Generate a strong random password if not provided
        password ??= GeneratePassword();

        var userInfo = new NetApi.UserInfo1
        {
            usri1_name = username,
            usri1_password = password,
            usri1_priv = NetApi.USER_PRIV_USER,
            usri1_flags = NetApi.UF_SCRIPT | NetApi.UF_NORMAL_ACCOUNT | NetApi.UF_DONT_EXPIRE_PASSWD,
            usri1_comment = "MultiSeat multi-seat account (managed)",
        };

        var result = NetApi.NetUserAdd(null, 1, ref userInfo, out var paramErr);

        if (result == NetApi.NERR_UserExists)
            throw new ResourceConflictException($"Windows account '{username}' already exists.");

        if (result != NetApi.NERR_Success)
            throw new InvalidOperationException(
                $"NetUserAdd failed for '{username}': error {result}, param {paramErr}");

        _logger.LogInformation("Created Windows account: {User}", username);

        ApplySeatGroupMembership(username);

        // Pre-create the user profile so the first RDP login doesn't stall
        // while Windows initializes the profile directory (can exceed the 15s timeout).
        EnsureUserProfile(username);

        // Store credential for session launching
        _credentials[username] = password;

        var account = new AccountInfo
        {
            Username = username,
            IsManaged = true
        };

        _managedAccounts.TryAdd(username, account);
        SavePersistedAccounts();
        return account;
    }

    /// <summary>
    /// Link an existing Windows local account to MultiSeat (no new account created).
    /// </summary>
    public AccountInfo LinkExistingAccount(string username, string password)
    {
        if (_managedAccounts.ContainsKey(username))
            throw new InvalidOperationException($"Account '{username}' is already linked.");

        // Verify the Windows account actually exists
        var checkResult = NetApi.NetUserGetInfo(null, username, 1, out var infoPtr);
        if (checkResult != NetApi.NERR_Success)
            throw new InvalidOperationException($"Windows account '{username}' not found (error {checkResult}).");
        NetApi.NetApiBufferFree(infoPtr);

        // Store credential for session launching
        _credentials[username] = password;

        var account = new AccountInfo
        {
            Username = username,
            IsManaged = false  // not created by MultiSeat, just linked
        };

        _managedAccounts.TryAdd(username, account);
        SavePersistedAccounts();
        _logger.LogInformation("Linked existing Windows account: {User}", username);
        return account;
    }

    /// <summary>
    /// Delete a MultiSeat-managed account or unlink an existing account.
    /// Only deletes the Windows account if it was created by MultiSeat (IsManaged=true).
    /// </summary>
    public void DeleteAccount(string username)
    {
        if (!_managedAccounts.TryGetValue(username, out var account))
            throw new InvalidOperationException($"Account '{username}' is not managed by MultiSeat.");

        if (account.IsManaged)
        {
            // Only delete the actual Windows account if MultiSeat created it
            var result = NetApi.NetUserDel(null, username);
            if (result != NetApi.NERR_Success && result != NetApi.NERR_UserNotFound)
                throw new InvalidOperationException(
                    $"NetUserDel failed for '{username}': error {result}");
            _logger.LogInformation("Deleted Windows account: {User}", username);
        }
        else
        {
            _logger.LogInformation("Unlinked existing account: {User} (Windows account preserved)", username);
        }

        _managedAccounts.TryRemove(username, out _);
        _credentials.TryRemove(username, out _);
        SavePersistedAccounts();
    }

    /// <summary>
    /// Put a seat account in the groups a seat actually needs, and take away the one it does not.
    ///
    /// A seat needs <c>Users</c> and <c>Remote Desktop Users</c>. The second is what makes the RDP
    /// loopback logon work; it was never granted before because membership of Administrators
    /// implies it, so removing admin without adding it would stop every seat from starting.
    ///
    /// It does NOT need Administrators. See <see cref="MultiSeatOptions.GrantSeatAdministrator"/>
    /// for the evidence — the SudoVDA justification does not survive either reading the driver's
    /// INF or trying it with a non-admin account.
    ///
    /// Idempotent, and called on provisioning as well as creation so a seat created by an older
    /// build gets corrected rather than staying an administrator forever.
    /// </summary>
    public void ApplySeatGroupMembership(string username)
    {
        AddToGroup(username, UsersGroup);
        AddToGroup(username, RemoteDesktopUsersGroup);

        if (_grantAdministrator)
        {
            _logger.LogWarning(
                "Seat account '{User}' is being added to {Group} because " +
                "MultiSeat:GrantSeatAdministrator is enabled — the seat can control this host and " +
                "read MultiSeat's own credential store.", username, AdministratorsGroup);
            AddToGroup(username, AdministratorsGroup);
            return;
        }

        // Only ever demote accounts MultiSeat created. A linked account is someone's real Windows
        // login — quite possibly the operator's own — and stripping its privileges because it was
        // pointed at a seat would be an unpleasant surprise.
        if (_managedAccounts.TryGetValue(username, out var account) && !account.IsManaged)
        {
            _logger.LogDebug(
                "Leaving group membership of linked account '{User}' alone — not MultiSeat-created.",
                username);
            return;
        }

        RemoveFromGroup(username, AdministratorsGroup);
    }

    /// <summary>
    /// Bring every MultiSeat-created account's group membership in line with the current policy.
    /// Called at service startup so an install upgraded from a build that made seats
    /// administrators is corrected without waiting for the next provision.
    /// </summary>
    public void NormalizeManagedAccountPrivileges()
    {
        foreach (var account in _managedAccounts.Values.Where(a => a.IsManaged))
        {
            try
            {
                ApplySeatGroupMembership(account.Username);
            }
            catch (Exception ex)
            {
                // One bad account must not stop the service starting.
                _logger.LogWarning(ex,
                    "Could not normalise group membership for seat account '{User}'.",
                    account.Username);
            }
        }
    }

    private void AddToGroup(string username, string groupName)
    {
        var memberInfo = new NetApi.LocalGroupMembersInfo3
        {
            lgrmi3_domainandname = $"{Environment.MachineName}\\{username}"
        };

        var result = NetApi.NetLocalGroupAddMembers(null, groupName, 3, ref memberInfo, 1);

        if (result != NetApi.NERR_Success && result != NetApi.ERROR_MEMBER_IN_ALIAS)
        {
            _logger.LogWarning("NetLocalGroupAddMembers({User} → {Group}) failed: {Err}",
                username, groupName, result);
        }
    }

    private void RemoveFromGroup(string username, string groupName)
    {
        var memberInfo = new NetApi.LocalGroupMembersInfo3
        {
            lgrmi3_domainandname = $"{Environment.MachineName}\\{username}"
        };

        var result = NetApi.NetLocalGroupDelMembers(null, groupName, 3, ref memberInfo, 1);

        if (result == NetApi.NERR_Success)
        {
            // Worth an Information rather than a Debug: this is a privilege change to a Windows
            // account, and it is the kind of thing someone reading the log after an upgrade should
            // be able to find.
            _logger.LogInformation(
                "Removed seat account '{User}' from {Group} — seats do not need administrator " +
                "rights (set MultiSeat:GrantSeatAdministrator if a specific setup does).",
                username, groupName);
        }
        else if (result != NetApi.ERROR_MEMBER_NOT_IN_ALIAS)
        {
            _logger.LogWarning("NetLocalGroupDelMembers({User} → {Group}) failed: {Err}",
                username, groupName, result);
        }
    }

    private void DiscoverExistingAccounts()
    {
        // Scan for existing MultiSeatSeat* accounts on startup
        var resumeHandle = IntPtr.Zero;
        var result = NetApi.NetUserEnum(null, 1, 0, out var bufPtr,
            -1, out var entriesRead, out _, ref resumeHandle);

        if (result != NetApi.NERR_Success || bufPtr == IntPtr.Zero)
            return;

        try
        {
            var structSize = Marshal.SizeOf<NetApi.UserInfo1>();
            for (int i = 0; i < entriesRead; i++)
            {
                var info = Marshal.PtrToStructure<NetApi.UserInfo1>(bufPtr + i * structSize);
                if (info.usri1_name.StartsWith(Constants.AccountPrefix, StringComparison.OrdinalIgnoreCase)
                    && info.usri1_comment?.Contains("MultiSeat") == true)
                {
                    _managedAccounts.TryAdd(info.usri1_name, new AccountInfo
                    {
                        Username = info.usri1_name,
                        IsManaged = true
                    });
                }
            }

            if (_managedAccounts.Count > 0)
                _logger.LogInformation("Discovered {Count} existing MultiSeat accounts", _managedAccounts.Count);
        }
        finally
        {
            NetApi.NetApiBufferFree(bufPtr);
        }
    }

    /// <summary>
    /// Save all credentials to disk encrypted with DPAPI, scoped to the account the service runs
    /// as (SYSTEM). Survives service restarts; decryptable only by SYSTEM on this machine.
    /// </summary>
    /// <remarks>
    /// The scope used to be <see cref="DataProtectionScope.LocalMachine"/>, which any process on
    /// the box could decrypt regardless of which user it ran as — so the seat passwords were
    /// protected by nothing but the file's ACL, and that ACL granted BUILTIN\Users read. CurrentUser
    /// under SYSTEM ties the blob to SYSTEM's master key, so a copy of accounts.json is useless to
    /// a non-SYSTEM reader.
    ///
    /// The honest limit: an Administrator can obtain SYSTEM, and today every seat account is in
    /// Administrators, so this does not yet defend against a seat. It defends against every
    /// non-admin local account, and against the file being read from a backup or copied off the
    /// machine. Narrowing seat accounts is a separate open item.
    ///
    /// Consequence to be aware of: blobs are now bound to SYSTEM. If the service is ever
    /// reconfigured to run as a different account, stored credentials become undecryptable and
    /// seats must be re-provisioned. LoadPersistedAccounts logs that case explicitly.
    /// </remarks>
    private void SavePersistedAccounts()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var entries = _credentials.Select(kv =>
            {
                var encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(kv.Value),
                    null, DataProtectionScope.CurrentUser);
                var isManaged = _managedAccounts.TryGetValue(kv.Key, out var acc) && acc.IsManaged;
                return new StoredAccount(kv.Key, Convert.ToBase64String(encrypted), isManaged,
                                         ScopeTagUser);
            }).ToList();

            // Write-then-rename so a crash mid-write can't corrupt the credential store.
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));

            // Restrict the temp file BEFORE the rename, so there is no window in which a
            // world-readable copy of the credential store exists under ProgramData. Disabled
            // inheritance is part of the file's own security descriptor and survives the move.
            HardenStore(tmp);

            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist account credentials to {Path}", StorePath);
        }
    }

    /// <summary>
    /// Strip the inherited BUILTIN\Users grant from the credential store so only SYSTEM and
    /// Administrators can read it. Best-effort: a failure here is worth a warning, not a crash.
    /// </summary>
    private void HardenStore(string path) =>
        Storage.SecureFile.TryRestrictToSystemAndAdmins(path, ex =>
            _logger.LogWarning(ex,
                "Could not restrict permissions on the credential store {Path} — it may still be " +
                "readable by other local accounts.", path));

    /// <summary>
    /// Load credentials from disk on startup.
    /// Restores both credentials and linked accounts that wouldn't be found by DiscoverExistingAccounts.
    /// </summary>
    private void LoadPersistedAccounts()
    {
        if (!File.Exists(StorePath)) return;

        // An install that predates the ACL tightening has a store the whole Users group can read.
        // No write path would revisit it on a host where no seat is ever added or removed, so fix
        // it on the read path — which runs on every service start.
        HardenStore(StorePath);

        try
        {
            var entries = JsonSerializer.Deserialize<List<StoredAccount>>(File.ReadAllText(StorePath));
            if (entries == null) return;

            // Set when any entry was still LocalMachine-scoped, so the store gets rewritten at
            // CurrentUser scope once the whole file has been read.
            var migrated = false;

            foreach (var entry in entries)
            {
                string password;
                try
                {
                    password = DecryptPassword(entry.EncryptedPassword);
                    if (IsLegacyScope(entry.Scope)) migrated = true;
                }
                catch (Exception ex)
                {
                    // Per-entry, deliberately: this loop used to share one try block with the
                    // whole method, so a single undecryptable entry silently discarded every
                    // credential after it. One unreadable seat should cost that seat, not the rest.
                    _logger.LogError(ex,
                        "Could not decrypt the stored credential for '{User}' — that seat will " +
                        "need re-provisioning. This is expected if the service now runs as a " +
                        "different account than the one that saved it.", entry.Username);
                    continue;
                }

                _credentials[entry.Username] = password;

                // Restore linked accounts not found by DiscoverExistingAccounts
                if (!_managedAccounts.ContainsKey(entry.Username))
                {
                    _managedAccounts.TryAdd(entry.Username, new AccountInfo
                    {
                        Username = entry.Username,
                        IsManaged = entry.IsManaged
                    });
                    _logger.LogInformation(
                        "Restored {Type} account '{User}' from credential store",
                        entry.IsManaged ? "managed" : "linked", entry.Username);
                }
                else
                {
                    _logger.LogDebug("Restored credential for '{User}'", entry.Username);
                }
            }

            if (migrated)
            {
                _logger.LogInformation(
                    "Re-encrypting the credential store at SYSTEM scope — it was written with " +
                    "machine scope, which any local account could have decrypted.");
                SavePersistedAccounts();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted accounts from {Path}", StorePath);
        }
    }

    /// <summary>
    /// Decrypts a stored password. Reads both the current SYSTEM-scoped format and the legacy
    /// machine-scoped one without being told which it is, because DPAPI recovers the scope from the
    /// blob and ignores the argument — the scope passed here is only what a *new* blob would use.
    /// </summary>
    internal static string DecryptPassword(string encrypted) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser));

    /// <summary>
    /// True when an entry predates the move to SYSTEM scope and should be rewritten. Driven by the
    /// recorded tag, never by a failed decryption — see <see cref="StoredAccount"/>.
    /// </summary>
    internal static bool IsLegacyScope(string? scopeTag) =>
        !string.Equals(scopeTag, ScopeTagUser, StringComparison.OrdinalIgnoreCase);

    private void EnsureUserProfile(string username)
    {
        try
        {
            var sid = new NTAccount(username).Translate(typeof(SecurityIdentifier)).ToString();
            var profilePath = new StringBuilder(260);
            var hr = UserEnv.CreateProfile(sid, username, profilePath, (uint)profilePath.Capacity);
            const int AlreadyExists = unchecked((int)0x80070050);
            if (hr == 0)
                _logger.LogInformation("Pre-created user profile for '{User}': {Path}", username, profilePath);
            else if (hr == AlreadyExists)
                _logger.LogDebug("User profile for '{User}' already exists", username);
            else
                _logger.LogWarning("CreateProfile for '{User}' returned HRESULT 0x{Hr:X8} (non-fatal)", username, hr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-create user profile for '{User}' (non-fatal)", username);
        }
    }

    private static string GeneratePassword()
    {
        // Generate a 24-char random password with mixed character classes
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
        return RandomNumberGenerator.GetString(chars, 24);
    }
}
