using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// GH #28 follow-up: a seat's explicit Modify ACE must survive the atomic rewrite of its
/// pairing state.
///
/// <see cref="AtomicFile"/> replaces a file by rename, and a renamed file is a NEW file object
/// carrying none of the explicit ACEs the old one had. <c>GrantSeatWrite</c> applies the seat's
/// Modify per file, so every AtomicFile call site has to re-grant afterwards. Two did;
/// <c>WriteStateFile</c> did not, because it was <c>static</c> and had no accountName to grant
/// with. The consequence is silent and delayed — after an unpair the seat's Apollo cannot write
/// its own pairing state, and nothing fails until the NEXT pairing request.
///
/// <see cref="ApolloConfigAtomicTests"/> pins the structural property (not static, takes an
/// accountName). This pins the behaviour it exists to produce, which is the thing that actually
/// matters, and it is the only test here that would catch a re-grant that was present but wrong.
///
/// ⚠️ The seat directory is deliberately set up the way a post-#28 host looks — protected, with
/// the account's Modify inheritable. That means the file also picks up an INHERITED Modify, so
/// the assertion is specifically about an **explicit** (non-inherited) entry. Asserting merely
/// "the account can write" would pass on a broken build, because inheritance alone satisfies it.
/// </summary>
public class SeatStateReGrantTests
{
    /// <summary>
    /// The account to act as. Any resolvable LOCAL account works — the production code does
    /// <c>new NTAccount(Environment.MachineName, accountName)</c> — so the current user is used
    /// rather than depending on a seat account existing on the machine.
    /// </summary>
    private static string? LocalAccountName()
    {
        var parts = WindowsIdentity.GetCurrent().Name.Split('\\');
        if (parts.Length != 2) return null;
        if (!string.Equals(parts[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return null; // domain account: NTAccount(machine, name) would not resolve

        try
        {
            _ = (SecurityIdentifier)new NTAccount(Environment.MachineName, parts[1])
                .Translate(typeof(SecurityIdentifier));
            return parts[1];
        }
        catch
        {
            return null;
        }
    }

    private static ApolloConfigBuilder NewBuilder() => new(
        NullLogger<ApolloConfigBuilder>.Instance,
        Options.Create(new MultiSeatOptions()));

    [Fact]
    public void UnpairAllClients_ReGrantsTheSeatsExplicitAce_afterTheAtomicRename()
    {
        var account = LocalAccountName();
        // Loud rather than a silent skip: a vacuous pass on an unsupported host would be worse
        // than no test, and windows-latest runners use a local account.
        Assert.True(account is not null,
            "Could not resolve a local account for the current user, so this test cannot run. "
            + "It needs an account that NTAccount(MachineName, name) resolves.");

        var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, account!)
            .Translate(typeof(SecurityIdentifier));

        var root = Path.Combine(Path.GetTempPath(), "ms-regrant-" + Guid.NewGuid().ToString("N"));
        var seatDir = Path.Combine(root, account!);
        var configDir = Path.Combine(seatDir, "config");
        Directory.CreateDirectory(configDir);

        try
        {
            // Make the seat dir look like a post-#28 host: protected, account Modify inheritable.
            var dirAcl = new DirectorySecurity();
            dirAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags Inherit = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;
            dirAcl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            dirAcl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            dirAcl.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(seatDir).SetAccessControl(dirAcl);

            // A device to clear, so the assertion below proves the method actually ran rather
            // than returning early.
            var statePath = Path.Combine(configDir, "sunshine_state.json");
            File.WriteAllText(
                statePath,
                """{"root":{"named_devices":[{"name":"probe","perm":119480064}]}}""",
                Encoding.UTF8);

            // Strip any explicit entry, so only a re-grant can put one back.
            var before = new FileInfo(statePath).GetAccessControl();
            foreach (FileSystemAccessRule r in before.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                before.RemoveAccessRuleSpecific(r);
            new FileInfo(statePath).SetAccessControl(before);

            Assert.False(
                HasExplicitAce(statePath, sid),
                "precondition: the state file must start with no explicit ACE for the account");

            NewBuilder().UnpairAllClients(account!, root);

            // Proves the method ran: it clears named_devices and rewrites the file.
            using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
            {
                Assert.Equal(
                    0,
                    doc.RootElement.GetProperty("root").GetProperty("named_devices").GetArrayLength());
            }

            Assert.True(
                HasExplicitAce(statePath, sid),
                "the atomic rename dropped the seat's explicit Modify ACE and it was not re-granted; "
                + "after an unpair the seat's Apollo cannot write its own pairing state, and the "
                + "failure only surfaces on the next pairing request");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// An <b>explicit</b> (non-inherited) allow rule granting the SID write access. Inherited
    /// entries are ignored on purpose — on a post-#28 host the account inherits Modify from the
    /// directory, so counting those would pass on a build with no re-grant at all.
    /// </summary>
    private static bool HasExplicitAce(string path, SecurityIdentifier sid) =>
        new FileInfo(path)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                && r.IdentityReference.Equals(sid)
                && (r.FileSystemRights & FileSystemRights.Write) == FileSystemRights.Write);
}
