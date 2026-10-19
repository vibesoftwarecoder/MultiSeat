using System.Security.Principal;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using MultiSeat.Service.Accounts;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Interop;
using MultiSeat.Service.Sessions;
using MultiSeat.Service.Streaming;

namespace MultiSeat.Service.SteamShim;

/// <summary>
/// Sends the ordinary ways of starting Steam through <c>MultiSeat.SteamShim.exe</c>, so that a click
/// on a Steam shortcut, a <c>steam://</c> link or the Run entry starts an isolated Steam, or hands its
/// command to the isolated one already running, and never starts a plain Steam (docs/design/steam-shim.md).
///
/// What it edits, and where:
/// <list type="bullet">
/// <item>the shared Steam shortcuts (Public Desktop, All Users Start Menu): machine-wide, so the console
///   user's click and a seat's click land on the same shim;</item>
/// <item>per account (each seat, and the console user unless <see cref="MultiSeatOptions.SteamShimIncludeConsole"/>
///   is off): <c>HKCU\Software\Classes\steam</c> (the protocol handler, which outranks the machine-wide
///   one), the <c>Run\Steam</c> value if there is one, and that account's own Steam shortcuts.</item>
/// </list>
/// Every change is recorded in <see cref="SteamShimBackup"/> first, with what was there before, and
/// <see cref="Undo"/> puts it all back. Nothing is edited unless the shim, the launcher and the hooks
/// are all present: a shortcut that points at a missing shim would stop Steam for everyone.
///
/// Never throws. A failure is logged and costs only the edit it was about.
/// </summary>
public sealed class SteamShimInstaller
{
    internal const string ShimFileName = "MultiSeat.SteamShim.exe";
    private const string HandlerKey = @"Software\Classes\steam\shell\open\command";
    private const string ProtocolKey = @"Software\Classes\steam";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Steam";

    private readonly ILogger _logger;
    private readonly MultiSeatOptions _options;
    private readonly IShortcutStore _shortcuts;
    private readonly Lazy<SteamShimBackup> _backup;
    private readonly Func<string, (int ExitCode, string Error)> _runReg;
    private readonly object _gate = new();

    public SteamShimInstaller(ILogger<SteamShimInstaller> logger, IOptions<MultiSeatOptions> options)
        : this(logger, options.Value, new WScriptShortcutStore(),
               () => new SteamShimBackup(Shared.Constants.DefaultSteamShimBackupPath), AccountManager.RunRegExe)
    {
    }

    internal SteamShimInstaller(ILogger logger, MultiSeatOptions options, IShortcutStore shortcuts,
        Func<SteamShimBackup> backup, Func<string, (int ExitCode, string Error)> runReg)
    {
        _logger = logger;
        _options = options;
        _shortcuts = shortcuts;
        _backup = new Lazy<SteamShimBackup>(backup);
        _runReg = runReg;
        UserRootOpener = OpenUserRoot;
    }

    // ── Seams the tests replace; the service always uses the defaults ─────────────────────────────

    /// <summary>The shared shortcuts: the Public Desktop and the All Users Start Menu.</summary>
    internal Func<IEnumerable<string>> MachineShortcutPaths { get; set; } = () =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Steam.lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Steam", "Steam.lnk"),
    ];

    /// <summary>The account on the console, as a SID and profile folder, or null when nobody is signed in.</summary>
    internal Func<(string Sid, string Profile)?> ConsoleUserResolver { get; set; } = ResolveConsoleUser;

    /// <summary>A user's profile folder from the profile list, or null before the profile exists.</summary>
    internal Func<string, string?> ProfilePathResolver { get; set; } = ResolveProfilePath;

    /// <summary>Where Steam is when an account has no Steam entry to copy the path from.</summary>
    internal Func<string> SteamExecutableResolver { get; set; } = ApolloConfigBuilder.ResolveSteamExecutable;

    /// <summary>Opens an account's registry root (its HKCU) and says how to let go of it again.</summary>
    internal Func<string, string, string, (RegistryKey? Root, Action Release)> UserRootOpener { get; set; }

    internal string ShimPath =>
        Path.Combine(Path.GetDirectoryName(_options.SteamIsolationLauncherPath) ?? "", ShimFileName);

    // ── Public entry points ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The shared shortcuts and, unless switched off, the console user. Run when the service starts.
    /// When isolation or the shim is switched off while edits are still recorded, they are undone,
    /// so turning the feature off leaves the host as it was.
    /// </summary>
    public void ApplyToHost()
    {
        try
        {
            lock (_gate)
            {
                if (!_options.SteamIsolationEnabled || !_options.SteamShimEnabled)
                {
                    if (_backup.Value.Entries.Count > 0)
                    {
                        _logger.LogInformation(
                            "Steam isolation or the Steam shim is switched off, so the {Count} Steam shortcut and " +
                            "registry edits from earlier are being undone.", _backup.Value.Entries.Count);
                        UndoLocked();
                    }

                    return;
                }

                if (!Preflight()) return;
                var changed = 0;
                foreach (var path in MachineShortcutPaths()) changed += PatchShortcut(path, "shared") ? 1 : 0;

                if (_options.SteamShimIncludeConsole)
                {
                    if (ConsoleUserResolver() is { } console) changed += ApplyToSid(console.Sid, console.Profile, "console user");
                    else _logger.LogInformation("Steam shim: nobody is signed in on the console; its Steam entries are left for next start.");
                }

                _logger.LogInformation("Steam shim: {Count} shared and console Steam entries now go through the shim.", changed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Steam shim: could not set up the shared Steam shortcuts.");
        }
    }

    /// <summary>One account's Steam entries. Run at each seat provision, before its session starts.</summary>
    public void ApplyToAccount(string username)
    {
        try
        {
            if (!_options.SteamIsolationEnabled || !_options.SteamShimEnabled) return;
            lock (_gate)
            {
                if (!Preflight()) return;
                var sid = AccountManager.TryResolveSid(username, _logger);
                if (sid is null) return;
                var profile = ProfilePathResolver(sid);
                if (profile is null)
                {
                    _logger.LogInformation("Steam shim: '{User}' has no profile yet; its Steam entries are set up at the next provision.", username);
                    return;
                }

                ApplyToSid(sid, profile, username);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Steam shim: could not set up the Steam entries of '{User}'.", username);
        }
    }

    /// <summary>Puts every recorded shortcut and registry value back as it was.</summary>
    public int Undo()
    {
        lock (_gate) return UndoLocked();
    }

    // ── The work ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything for one account, given the root of its registry (its HKCU). Returns how many entries
    /// were changed. Internal so tests can hand it a scratch key.
    /// </summary>
    internal int ApplyToUser(RegistryKey root, string? sid, string label, string? profilePath)
    {
        var changed = 0;
        changed += PatchHandler(root, sid, label) ? 1 : 0;
        changed += PatchRun(root, sid, label) ? 1 : 0;
        if (profilePath is not null)
        {
            foreach (var path in UserShortcutPaths(profilePath)) changed += PatchShortcut(path, label) ? 1 : 0;
        }

        return changed;
    }

    private int ApplyToSid(string sid, string profilePath, string label)
    {
        var (root, release) = UserRootOpener(sid, profilePath, label);
        if (root is null) return 0;
        try
        {
            return ApplyToUser(root, sid, label, profilePath);
        }
        finally
        {
            release();
        }
    }

    private bool Preflight()
    {
        var directory = Path.GetDirectoryName(_options.SteamIsolationLauncherPath) ?? "";
        var needed = new[]
        {
            ShimPath, _options.SteamIsolationLauncherPath,
            Path.Combine(directory, "MultiSeat.SteamHook.dll"), Path.Combine(directory, "MultiSeat.SteamHook32.dll"),
        };
        var missing = needed.Where(p => !File.Exists(p)).ToList();
        if (missing.Count == 0) return true;
        _logger.LogError(
            "Steam shim: {Missing} is missing, so no Steam shortcut or registry entry was changed. A shortcut " +
            "that points at a missing shim would stop Steam for everyone. Reinstall MultiSeat to restore the " +
            "SteamIsolation folder.", string.Join(", ", missing));
        return false;
    }

    private static IEnumerable<string> UserShortcutPaths(string profile)
    {
        yield return Path.Combine(profile, "Desktop", "Steam.lnk");
        yield return Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Steam", "Steam.lnk");

        // Shortcuts Windows or the user made elsewhere: a pin on the taskbar, the "implicit" shortcut
        // Windows keeps for a running app's taskbar button, an entry in the Startup folder. One of these
        // started a plain Steam on the reference host (2026-10-19). Only the ones that point at Steam
        // are changed; PatchShortcut passes over everything else.
        var roaming = Path.Combine(profile, "AppData", "Roaming", "Microsoft");
        foreach (var folder in new[]
        {
            Path.Combine(roaming, "Internet Explorer", "Quick Launch", "User Pinned"),
            Path.Combine(roaming, "Windows", "Start Menu", "Programs", "Startup"),
        })
        {
            IEnumerable<string> found;
            try
            {
                found = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories).ToList()
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in found) yield return path;
        }
    }

    private bool PatchShortcut(string path, string label)
    {
        ShortcutInfo? info;
        try
        {
            info = _shortcuts.Read(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Steam shim: could not read the shortcut {Path}.", path);
            return false;
        }

        if (info is null) return false;
        if (string.Equals(info.Target, ShimPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (!SteamIsolationLaunchPlan.TryNormalizeSteamPath(info.Target, out var steam) || !Path.IsPathFullyQualified(steam))
            return false;   // some other shortcut that happens to be called Steam

        var entry = new ShimBackupEntry("shortcut", path, null, true, null, info);
        _backup.Value.TryRecord(entry);
        try
        {
            _shortcuts.Write(path, new ShortcutInfo(
                ShimPath, SteamShimCommand.ShimArguments(steam, info.Arguments), info.WorkingDirectory,
                SteamShimCommand.PinnedIcon(info.IconLocation, steam), info.Description));
            _logger.LogInformation("Steam shim: {Path} ({Label}) now starts Steam through the shim.", path, label);
            return true;
        }
        catch (Exception ex)
        {
            _backup.Value.Forget(entry);
            _logger.LogWarning(ex, "Steam shim: could not change the shortcut {Path}.", path);
            return false;
        }
    }

    private bool PatchHandler(RegistryKey root, string? sid, string label)
    {
        string? existing;
        using (var key = root.OpenSubKey(HandlerKey)) existing = key?.GetValue("") as string;
        if (SteamShimCommand.StartsShim(existing, ShimPath)) return false;

        string steam, rest;
        if (existing is null)
        {
            steam = SteamExecutableResolver();
            rest = "-- \"%1\"";
        }
        else if (!SteamShimCommand.TryParseSteam(existing, out steam, out rest))
        {
            _logger.LogWarning("Steam shim: the steam:// handler of {Label} is not Steam's ({Command}); it is left alone.", label, existing);
            return false;
        }

        if (!Path.IsPathFullyQualified(steam)) return false;
        _backup.Value.TryRecord(new ShimBackupEntry("registry", HandlerKey + "|", sid, existing is not null, existing, null));
        using (var key = root.CreateSubKey(HandlerKey, true)) key.SetValue("", SteamShimCommand.ShimCommand(ShimPath, steam, rest));
        if (existing is null)
        {
            // A protocol key made from nothing needs the marker that says it is a protocol.
            using var protocol = root.CreateSubKey(ProtocolKey, true);
            if (protocol.GetValue("URL Protocol") is null) protocol.SetValue("URL Protocol", "");
        }

        _logger.LogInformation("Steam shim: the steam:// handler of {Label} now goes through the shim.", label);
        return true;
    }

    private bool PatchRun(RegistryKey root, string? sid, string label)
    {
        string? existing;
        using (var key = root.OpenSubKey(RunKey)) existing = key?.GetValue(RunValue) as string;
        if (existing is null || SteamShimCommand.StartsShim(existing, ShimPath)) return false;   // no autostart: add none
        if (!SteamShimCommand.TryParseSteam(existing, out var steam, out var rest)) return false;

        _backup.Value.TryRecord(new ShimBackupEntry("registry", RunKey + "|" + RunValue, sid, true, existing, null));
        using (var key = root.CreateSubKey(RunKey, true)) key.SetValue(RunValue, SteamShimCommand.ShimCommand(ShimPath, steam, rest));
        _logger.LogInformation("Steam shim: the Steam autostart of {Label} now goes through the shim.", label);
        return true;
    }

    private int UndoLocked()
    {
        var restored = 0;
        var entries = _backup.Value.Entries;
        foreach (var entry in entries.Where(e => e.Kind == "shortcut"))
        {
            try
            {
                // A shortcut the user has deleted since is not brought back.
                if (entry.Shortcut is not null && _shortcuts.Read(entry.Location) is not null)
                {
                    _shortcuts.Write(entry.Location, entry.Shortcut);
                    restored++;
                }

                _backup.Value.Forget(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Steam shim: could not restore the shortcut {Path}; it stays recorded.", entry.Location);
            }
        }

        foreach (var perUser in entries.Where(e => e.Kind == "registry").GroupBy(e => e.Sid))
        {
            var sid = perUser.Key;
            var profile = sid is null ? null : ProfilePathResolver(sid);
            if (sid is null || profile is null)
            {
                _logger.LogWarning("Steam shim: the account {Sid} has no profile any more; its registry edits are forgotten.", sid);
                foreach (var gone in perUser) _backup.Value.Forget(gone);
                continue;
            }

            var (root, release) = UserRootOpener(sid, profile, "undo");
            if (root is null) continue;   // stays recorded for the next try
            try
            {
                foreach (var entry in perUser)
                {
                    try
                    {
                        RestoreRegistry(root, entry);
                        _backup.Value.Forget(entry);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Steam shim: could not restore {Location}; it stays recorded.", entry.Location);
                    }
                }
            }
            finally
            {
                release();
            }
        }

        _logger.LogInformation("Steam shim: {Count} entries restored to what they were before.", restored);
        return restored;
    }

    private static void RestoreRegistry(RegistryKey root, ShimBackupEntry entry)
    {
        var bar = entry.Location.LastIndexOf('|');
        var keyPath = entry.Location[..bar];
        var valueName = entry.Location[(bar + 1)..];
        if (entry.Existed)
        {
            using var key = root.CreateSubKey(keyPath, true);
            key.SetValue(valueName, entry.Value ?? "");
        }
        else if (keyPath == HandlerKey)
        {
            root.DeleteSubKeyTree(ProtocolKey, false);   // the whole protocol key was ours
        }
        else
        {
            using var key = root.OpenSubKey(keyPath, true);
            key?.DeleteValue(valueName, false);
        }
    }

    // ── Production implementations of the seams ───────────────────────────────────────────────────

    private (RegistryKey? Root, Action Release) OpenUserRoot(string sid, string profilePath, string label)
    {
        // Signed in: the account's hive is already loaded under HKEY_USERS\<sid>.
        var loaded = Registry.Users.OpenSubKey(sid, writable: true);
        if (loaded is not null) return (loaded, loaded.Dispose);

        var hive = Path.Combine(profilePath, "NTUSER.DAT");
        if (!File.Exists(hive))
        {
            _logger.LogInformation("Steam shim: {Hive} does not exist yet, so '{User}' is set up later.", hive, label);
            return (null, () => { });
        }

        var mount = AccountManager.HiveMountName(sid);
        var load = _runReg(AccountManager.RegLoadArguments(mount, hive));
        if (load.ExitCode != 0)
        {
            _logger.LogWarning("Steam shim: could not load the registry of '{User}' (reg load exited {Code}: {Err}).",
                label, load.ExitCode, load.Error.Trim());
            return (null, () => { });
        }

        var root = Registry.Users.OpenSubKey("MultiSeat-" + sid, writable: true);
        return (root, () =>
        {
            root?.Dispose();
            AccountManager.UnloadHive(mount, label, _logger, _runReg);
        });
    }

    private static string? ResolveProfilePath(string sid)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        return key?.GetValue("ProfileImagePath") is string path ? Environment.ExpandEnvironmentVariables(path) : null;
    }

    private static (string Sid, string Profile)? ResolveConsoleUser()
    {
        var session = (int)Kernel32.WTSGetActiveConsoleSessionId();
        if (session < 0) return null;
        var user = QuerySession(session, WtsApi.WtsInfoClass.WTSUserName);
        var domain = QuerySession(session, WtsApi.WtsInfoClass.WTSDomainName);
        if (string.IsNullOrEmpty(user)) return null;
        var sid = new NTAccount(string.IsNullOrEmpty(domain) ? user : domain, user)
            .Translate(typeof(SecurityIdentifier)).Value;
        var profile = ResolveProfilePath(sid);
        return profile is null ? null : (sid, profile);
    }

    private static string? QuerySession(int session, WtsApi.WtsInfoClass info)
    {
        if (!WtsApi.WTSQuerySessionInformationW(WtsApi.WTS_CURRENT_SERVER_HANDLE, session, info, out var buffer, out _))
            return null;
        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }
    }
}
