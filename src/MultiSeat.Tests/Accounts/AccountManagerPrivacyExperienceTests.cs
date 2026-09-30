using Microsoft.Extensions.Logging;
using MultiSeat.Service.Accounts;
using Xunit;

namespace MultiSeat.Tests.Accounts;

/// <summary>
/// Issue #80: a new seat account's first logon waited for Windows' full-screen privacy settings
/// screen (CloudExperienceHost, <c>ms-cxh://NTHPRIVACY</c>), and while it did Apollo could not
/// open the input desktop, so the first provision could time out. <c>CreateAccount</c> now sets
/// <c>DisablePrivacyExperience = 1</c> in the new account's own hive via <c>reg.exe</c>.
///
/// These tests replace reg.exe with a recorder, so they check the command lines and the order
/// of load, add and unload, including the failure paths. They do NOT show that Windows honours
/// the value at the first logon. That needs a genuinely new account and a real logon, and was
/// verified live against real seat provisions (see the issue and the commit message).
/// </summary>
public class AccountManagerPrivacyExperienceTests : IDisposable
{
    private const string Sid = "S-1-5-21-111111111-222222222-333333333-1234";

    private readonly string _profileDir =
        Path.Combine(Path.GetTempPath(), $"MultiSeatPrivacyTest-{Guid.NewGuid():N}");

    public AccountManagerPrivacyExperienceTests()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllBytes(Path.Combine(_profileDir, "NTUSER.DAT"), [0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_profileDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Stands in for reg.exe: records each command line and answers by verb.</summary>
    private sealed class FakeReg
    {
        public List<string> Calls { get; } = new();
        public int LoadExit { get; init; }
        public int AddExit { get; init; }
        public int UnloadExit { get; init; }
        public bool ThrowOnAdd { get; init; }

        public (int ExitCode, string Error) Run(string arguments)
        {
            Calls.Add(arguments);
            var verb = arguments.Split(' ')[0];
            if (verb == "add" && ThrowOnAdd)
                throw new InvalidOperationException("simulated failure");
            var code = verb switch
            {
                "load" => LoadExit,
                "add" => AddExit,
                "unload" => UnloadExit,
                _ => throw new InvalidOperationException($"unexpected reg verb '{verb}'"),
            };
            return (code, code == 0 ? "" : "ERROR: simulated");
        }

        public IEnumerable<string> Verbs => Calls.Select(c => c.Split(' ')[0]);
    }

    [Fact]
    public void HiveMountName_IsUnderHkuAndCarriesTheSid()
    {
        Assert.Equal(@"HKU\MultiSeat-" + Sid, AccountManager.HiveMountName(Sid));
    }

    [Fact]
    public void RegArguments_QuoteBothPaths_AndSetTheDwordToOne()
    {
        var mount = AccountManager.HiveMountName(Sid);

        Assert.Equal(
            $"load \"{mount}\" \"C:\\Users\\Some Seat\\NTUSER.DAT\"",
            AccountManager.RegLoadArguments(mount, @"C:\Users\Some Seat\NTUSER.DAT"));
        Assert.Equal(
            $"add \"{mount}\\Software\\Policies\\Microsoft\\Windows\\OOBE\" " +
            "/v DisablePrivacyExperience /t REG_DWORD /d 1 /f",
            AccountManager.RegAddPrivacyPolicyArguments(mount));
        Assert.Equal($"unload \"{mount}\"", AccountManager.RegUnloadArguments(mount));
    }

    [Fact]
    public void Success_LoadsAddsAndUnloads_InThatOrder_AndLogsInformation()
    {
        var reg = new FakeReg();
        var logger = new RecordingLogger();

        AccountManager.SuppressPrivacyExperience(Sid, _profileDir, "NewSeat", logger, reg.Run);

        Assert.Equal(new[] { "load", "add", "unload" }, reg.Verbs);
        Assert.Contains(Path.Combine(_profileDir, "NTUSER.DAT"), reg.Calls[0]);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("NewSeat") && e.Message.Contains("DisablePrivacyExperience"));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void FailedAdd_StillUnloadsTheHive_AndWarns()
    {
        var reg = new FakeReg { AddExit = 1 };
        var logger = new RecordingLogger();

        AccountManager.SuppressPrivacyExperience(Sid, _profileDir, "NewSeat", logger, reg.Run);

        Assert.Equal(new[] { "load", "add", "unload" }, reg.Verbs);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("#80"));
    }

    [Fact]
    public void ThrowingAdd_StillUnloadsTheHive_AndDoesNotThrow()
    {
        var reg = new FakeReg { ThrowOnAdd = true };
        var logger = new RecordingLogger();

        AccountManager.SuppressPrivacyExperience(Sid, _profileDir, "NewSeat", logger, reg.Run);

        Assert.Equal(new[] { "load", "add", "unload" }, reg.Verbs);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void FailedLoad_DoesNotAddOrUnload_AndWarns()
    {
        var reg = new FakeReg { LoadExit = 1 };
        var logger = new RecordingLogger();

        AccountManager.SuppressPrivacyExperience(Sid, _profileDir, "NewSeat", logger, reg.Run);

        Assert.Equal(new[] { "load" }, reg.Verbs);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("#80"));
    }

    [Fact]
    public void FailedUnload_IsRetried_ThenReportedWithTheManualCommand()
    {
        var reg = new FakeReg { UnloadExit = 1 };
        var logger = new RecordingLogger();

        AccountManager.SuppressPrivacyExperience(Sid, _profileDir, "NewSeat", logger, reg.Run);

        Assert.Equal(new[] { "load", "add", "unload", "unload", "unload" }, reg.Verbs);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("reg unload") && e.Message.Contains(AccountManager.HiveMountName(Sid)));
    }

    [Fact]
    public void MissingHiveFile_RunsNothing_AndWarns()
    {
        var reg = new FakeReg();
        var logger = new RecordingLogger();
        var empty = Path.Combine(_profileDir, "no-profile-here");

        AccountManager.SuppressPrivacyExperience(Sid, empty, "NewSeat", logger, reg.Run);

        Assert.Empty(reg.Calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("does not exist"));
    }

    [Fact]
    public void MalformedSid_RunsNothing_AndWarns()
    {
        var reg = new FakeReg();
        var logger = new RecordingLogger();

        AccountManager.SuppressPrivacyExperience("S-1-5\" & calc", _profileDir, "NewSeat", logger, reg.Run);

        Assert.Empty(reg.Calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("does not look like a SID"));
    }
}
