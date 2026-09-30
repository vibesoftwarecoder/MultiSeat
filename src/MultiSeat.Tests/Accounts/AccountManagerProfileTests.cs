using Microsoft.Extensions.Logging;
using MultiSeat.Service.Accounts;
using Xunit;

namespace MultiSeat.Tests.Accounts;

/// <summary>
/// Issue #77: <c>AccountManager.DeleteAccount</c> called <c>NetUserDel</c> and nothing else, so
/// the Windows profile directory under <c>C:\Users\&lt;name&gt;</c> and its <c>ProfileList</c>
/// registry entry were left behind on every deletion.
///
/// The fix (<see cref="AccountManager.RemoveUserProfile"/> and
/// <see cref="AccountManager.TryResolveSid"/>) is deliberately factored out as static methods
/// that take an explicit <see cref="ILogger"/>, so the decision logic can be exercised here
/// without constructing a real <see cref="AccountManager"/> (whose constructor enumerates real
/// Windows accounts and reads/writes the on-disk credential store) and without needing an actual
/// account whose profile has been materialized by a real logon.
///
/// What these tests do NOT cover: actually deleting a live, loaded profile, or the "profile still
/// in use" failure path — both need a real Windows profile that only exists after an interactive
/// or RDP logon, which this test host does not have. That is covered by live verification against
/// a real MultiSeat seat instead (see the issue).
/// </summary>
public class AccountManagerProfileTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public void RemoveUserProfile_RejectsAMalformedSid_WithoutThrowing()
    {
        // Defense in depth: sid is always our own SecurityIdentifier.ToString() in production,
        // but this value is interpolated straight into a WQL query, so a value that does not
        // look like a SID must be refused before it ever reaches ManagementObjectSearcher.
        var logger = new RecordingLogger();

        AccountManager.RemoveUserProfile("not-a-sid; DROP TABLE Win32_UserProfile", "SomeSeat", logger);

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("does not look like a SID"));
    }

    [Fact]
    public void RemoveUserProfile_IsANoOp_WhenNoProfileMatchesTheSid()
    {
        // A well-formed SID that (barring astronomical coincidence) matches no real profile on
        // this machine — the same situation as an account that was created but never logged
        // into, which issue #77 explicitly calls out as a normal case, not a failure.
        const string neverLoggedInSid = "S-1-5-21-999999999-999999999-999999999-999999";
        var logger = new RecordingLogger();

        AccountManager.RemoveUserProfile(neverLoggedInSid, "NeverLoggedInSeat", logger);

        // Must not throw (asserted implicitly by reaching this line) and must not be reported
        // as a Warning — "no profile" is not an error.
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("nothing to remove"));
    }

    [Fact]
    public void TryResolveSid_ReturnsNull_ForAnUnknownAccountName()
    {
        // The exact situation DeleteAccount hits for an account NetUserDel is about to report
        // NERR_UserNotFound for: nothing to translate, and that must not throw or bubble up.
        var logger = new RecordingLogger();
        var unknownName = $"MultiSeatSeatNoSuchAccount{Guid.NewGuid():N}";

        var sid = AccountManager.TryResolveSid(unknownName, logger);

        Assert.Null(sid);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public void TryResolveSid_ResolvesAKnownBuiltinName_ToASidString()
    {
        // Sanity check of the happy path using a name guaranteed to exist on every Windows
        // install (a domain account would need a live seat, which this test host doesn't have).
        var logger = new RecordingLogger();

        var sid = AccountManager.TryResolveSid("Guests", logger);

        Assert.NotNull(sid);
        Assert.StartsWith("S-1-", sid);
    }

    [Fact(Skip = "Needs a real, logged-into Windows profile plus WMI delete rights — " +
                 "exercised by live verification against an actual seat account (issue #77), " +
                 "not by this test host.")]
    public void RemoveUserProfile_DeletesTheProfileDirectoryAndProfileListEntry_ForARealProfile()
    {
    }
}
