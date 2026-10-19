using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.SteamShim;
using Xunit;

namespace MultiSeat.Tests.SteamShim;

/// <summary>
/// The installer against a scratch registry key (standing in for an account's HKCU) and an in-memory
/// shortcut store, so nothing on the machine is touched. The checks are about behaviour: what the
/// entries say afterwards, what the backup keeps, and that undoing restores the first original.
/// </summary>
public sealed class SteamShimInstallerTests : IDisposable
{
    private const string Steam = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string HandlerKey = @"Software\Classes\steam\shell\open\command";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MultiSeat Shim " + Guid.NewGuid());
    private readonly string _isolation;
    private readonly string _shim;
    private readonly string _scratchPath = @"Software\MultiSeat\ShimTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _scratch;
    private readonly FakeShortcuts _shortcuts = new();
    private readonly ListLogger _log = new();
    private readonly string _backupPath;

    public SteamShimInstallerTests()
    {
        _isolation = Path.Combine(_root, "SteamIsolation");
        Directory.CreateDirectory(_isolation);
        _shim = Path.Combine(_isolation, "MultiSeat.SteamShim.exe");
        _backupPath = Path.Combine(_root, "backup.json");
        _scratch = Registry.CurrentUser.CreateSubKey(_scratchPath);
        PutAll();
    }

    public void Dispose()
    {
        _scratch.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_scratchPath, false);
        Directory.Delete(_root, recursive: true);
    }

    private void PutAll()
    {
        foreach (var name in new[] { "MultiSeat.SteamShim.exe", "MultiSeat.SteamLauncher.exe", "MultiSeat.SteamHook.dll", "MultiSeat.SteamHook32.dll" })
            File.WriteAllText(Path.Combine(_isolation, name), "x");
    }

    private SteamShimInstaller Installer(Action<MultiSeatOptions>? tweak = null)
    {
        var options = new MultiSeatOptions
        {
            SteamIsolationEnabled = true,
            SteamShimEnabled = true,
            SteamShimIncludeConsole = true,
            SteamIsolationLauncherPath = Path.Combine(_isolation, "MultiSeat.SteamLauncher.exe"),
        };
        tweak?.Invoke(options);
        return new SteamShimInstaller(_log, options, _shortcuts, () => new SteamShimBackup(_backupPath), _ => (0, ""))
        {
            SteamExecutableResolver = () => Steam,
            ConsoleUserResolver = () => null,   // never ask Windows who is on this machine's console
            ProfilePathResolver = sid => Path.Combine(_root, "profiles", sid),
            UserRootOpener = (_, _, _) => (_scratch, () => { }),
        };
    }

    private string? Handler() { using var k = _scratch.OpenSubKey(HandlerKey); return k?.GetValue("") as string; }
    private string? Run() { using var k = _scratch.OpenSubKey(RunKey); return k?.GetValue("Steam") as string; }
    private void SetHandler(string value) { using var k = _scratch.CreateSubKey(HandlerKey); k.SetValue("", value); }
    private void SetRun(string value) { using var k = _scratch.CreateSubKey(RunKey); k.SetValue("Steam", value); }
    private string Shimmed(string rest) => $"\"{_shim}\" --steam \"{Steam}\" -- {rest}".TrimEnd();

    // ── steam:// handler ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheHandlerOfAnAccountThatHasSteamGoesThroughTheShim()
    {
        SetHandler($"\"{Steam}\" -- \"%1\"");

        Assert.Equal(1, Installer().ApplyToUser(_scratch, "S-1-5-21-1", "seat", null));

        Assert.Equal(Shimmed("-- \"%1\""), Handler());
    }

    [Fact]
    public void AnAccountWithoutAHandlerGetsOneAndUndoRemovesWhatWasMade()
    {
        var installer = Installer();
        Assert.Equal(1, installer.ApplyToUser(_scratch, "S-1-5-21-1", "seat", null));

        Assert.Equal(Shimmed("-- \"%1\""), Handler());
        using (var protocol = _scratch.OpenSubKey(@"Software\Classes\steam"))
            Assert.NotNull(protocol?.GetValue("URL Protocol"));

        Assert.True(installer.Undo() >= 1);
        Assert.Null(_scratch.OpenSubKey(@"Software\Classes\steam"));
    }

    [Fact]
    public void AHandlerThatIsNotSteamsIsLeftAlone()
    {
        SetHandler(@"""C:\Other\launcher.exe"" ""%1""");

        Assert.Equal(0, Installer().ApplyToUser(_scratch, "S-1-5-21-1", "seat", null));

        Assert.Equal(@"""C:\Other\launcher.exe"" ""%1""", Handler());
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not Steam's"));
    }

    [Fact]
    public void ApplyingTwiceKeepsTheFirstOriginalAndUndoRestoresIt()
    {
        var original = $"\"{Steam}\" -- \"%1\"";
        SetHandler(original);
        var installer = Installer();

        installer.ApplyToUser(_scratch, "S-1-5-21-1", "seat", null);
        Assert.Equal(0, installer.ApplyToUser(_scratch, "S-1-5-21-1", "seat", null));   // nothing to do the second time
        var afterTwo = Handler();

        Assert.Equal(1, installer.Undo());
        Assert.Equal(original, Handler());
        Assert.NotEqual(original, afterTwo);   // the control: the apply did change it
    }

    // ── autostart ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnExistingAutostartGoesThroughTheShimAndNoneIsAddedWhereThereWasNone()
    {
        var installer = Installer();
        installer.ApplyToUser(_scratch, "S-1-5-21-1", "seat", null);
        Assert.Null(Run());   // an account that did not start Steam at logon does not start one now

        SetRun($"\"{Steam}\" -silent");
        installer.ApplyToUser(_scratch, "S-1-5-21-1", "seat", null);
        Assert.Equal(Shimmed("-silent"), Run());

        installer.Undo();
        Assert.Equal($"\"{Steam}\" -silent", Run());
    }

    // ── shortcuts ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASteamShortcutIsRepointedAtTheShimAndKeepsItsIcon()
    {
        var path = Path.Combine(_root, "Steam.lnk");
        _shortcuts.Files[path] = new ShortcutInfo(Steam, "-silent", @"C:\Program Files (x86)\Steam", "", "Steam");

        var installer = Installer();
        installer.MachineShortcutPaths = () => [path];
        installer.ApplyToHost();

        var now = _shortcuts.Files[path];
        Assert.Equal(_shim, now.Target);
        Assert.Equal($"--steam \"{Steam}\" -- -silent", now.Arguments);
        Assert.Equal($"{Steam},0", now.IconLocation);
        Assert.Equal(@"C:\Program Files (x86)\Steam", now.WorkingDirectory);

        installer.Undo();
        Assert.Equal(new ShortcutInfo(Steam, "-silent", @"C:\Program Files (x86)\Steam", "", "Steam"), _shortcuts.Files[path]);
    }

    [Fact]
    public void TaskbarPinsAndStartupShortcutsThatPointAtSteamGoThroughTheShimToo()
    {
        // A plain Steam started from the taskbar's implicit shortcut on the reference host (2026-10-19)
        // overwrote SteamPID and made the next launch start a second isolated Steam.
        var profile = Path.Combine(_root, "profile");
        var pinned = Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned");
        var implicitSteam = Path.Combine(pinned, "ImplicitAppShortcuts", "d356105fac5527ef", "Steam.lnk");
        var taskbarSteam = Path.Combine(pinned, "TaskBar", "Steam.lnk");
        var taskbarOther = Path.Combine(pinned, "TaskBar", "Firefox.lnk");
        var startup = Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Steam.lnk");
        var steamInfo = new ShortcutInfo(Steam, "", @"C:\Program Files (x86)\Steam", "", "Steam");
        var firefox = new ShortcutInfo(@"C:\Program Files\Mozilla Firefox\firefox.exe", "", "", "", "Firefox");
        foreach (var path in new[] { implicitSteam, taskbarSteam, taskbarOther, startup })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");   // the folder is listed from disk; the fake store holds what is inside
        }

        _shortcuts.Files[implicitSteam] = steamInfo;
        _shortcuts.Files[taskbarSteam] = steamInfo;
        _shortcuts.Files[startup] = steamInfo;
        _shortcuts.Files[taskbarOther] = firefox;

        var installer = Installer();
        Assert.Equal(3 + 1 /* the steam:// handler a seat without one gets */, installer.ApplyToUser(_scratch, "S-1-5-21-1", "console user", profile));

        foreach (var path in new[] { implicitSteam, taskbarSteam, startup })
            Assert.Equal(_shim, _shortcuts.Files[path].Target);
        Assert.Equal(firefox, _shortcuts.Files[taskbarOther]);

        installer.Undo();
        Assert.Equal(steamInfo, _shortcuts.Files[implicitSteam]);
    }

    [Fact]
    public void AShortcutThatIsNotSteamIsNotTouched()
    {
        var path = Path.Combine(_root, "Steam.lnk");
        var other = new ShortcutInfo(@"C:\Tools\EmuDeck.exe", "", @"C:\Tools", "", "Not Steam");
        _shortcuts.Files[path] = other;
        var installer = Installer();
        installer.MachineShortcutPaths = () => [path];
        installer.ApplyToHost();
        Assert.Equal(other, _shortcuts.Files[path]);
    }

    [Fact]
    public void AShortcutThatCannotBeWrittenIsNotLeftInTheBackup()
    {
        var path = Path.Combine(_root, "Steam.lnk");
        _shortcuts.Files[path] = new ShortcutInfo(Steam, "", @"C:\Steam", "", "Steam");
        _shortcuts.FailWrites = true;
        var installer = Installer();
        installer.MachineShortcutPaths = () => [path];

        installer.ApplyToHost();

        _shortcuts.FailWrites = false;
        Assert.Equal(Steam, _shortcuts.Files[path].Target);
        Assert.Equal(0, installer.Undo());   // nothing was recorded, so there is nothing to put back
    }

    // ── when it must do nothing ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WithAnyShimFileMissingNothingIsChangedAndTheLogSaysWhy()
    {
        File.Delete(Path.Combine(_isolation, "MultiSeat.SteamHook32.dll"));
        var path = Path.Combine(_root, "Steam.lnk");
        var before = new ShortcutInfo(Steam, "", @"C:\Steam", "", "Steam");
        _shortcuts.Files[path] = before;
        var installer = Installer();
        installer.MachineShortcutPaths = () => [path];

        installer.ApplyToHost();

        Assert.Equal(before, _shortcuts.Files[path]);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("MultiSeat.SteamHook32.dll"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WithIsolationOrTheShimSwitchedOffNothingIsChanged(bool isolation, bool shim)
    {
        var path = Path.Combine(_root, "Steam.lnk");
        var before = new ShortcutInfo(Steam, "", @"C:\Steam", "", "Steam");
        _shortcuts.Files[path] = before;
        var installer = Installer(o => { o.SteamIsolationEnabled = isolation; o.SteamShimEnabled = shim; });
        installer.MachineShortcutPaths = () => [path];

        installer.ApplyToHost();

        Assert.Equal(before, _shortcuts.Files[path]);
    }

    [Fact]
    public void SwitchingTheFeatureOffUndoesWhatWasDoneEarlier()
    {
        var path = Path.Combine(_root, "Steam.lnk");
        var before = new ShortcutInfo(Steam, "", @"C:\Steam", "", "Steam");
        _shortcuts.Files[path] = before;
        var on = Installer();
        on.MachineShortcutPaths = () => [path];
        on.ApplyToHost();
        Assert.NotEqual(before, _shortcuts.Files[path]);   // the control: it was changed

        var off = Installer(o => o.SteamShimEnabled = false);
        off.MachineShortcutPaths = () => [path];
        off.ApplyToHost();

        Assert.Equal(before, _shortcuts.Files[path]);
    }

    // ── the console user ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void TheConsoleUserIsIncludedUnlessSwitchedOff(bool includeConsole, int expectedLookups)
    {
        var lookups = 0;
        var installer = Installer(o => o.SteamShimIncludeConsole = includeConsole);
        installer.MachineShortcutPaths = () => [];
        installer.ConsoleUserResolver = () => { lookups++; return ("S-1-5-21-9", _root); };
        SetHandler($"\"{Steam}\" -- \"%1\"");

        installer.ApplyToHost();

        Assert.Equal(expectedLookups, lookups);
        Assert.Equal(includeConsole, Handler()!.StartsWith('"' + _shim, StringComparison.OrdinalIgnoreCase));
    }

    // ── the backup file ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBackupSurvivesARestartOfTheService()
    {
        var original = $"\"{Steam}\" -- \"%1\"";
        SetHandler(original);
        Installer().ApplyToUser(_scratch, "S-1-5-21-1", "seat", null);

        // A second installer reads the same file, as the service does after a restart.
        Assert.Equal(1, Installer().Undo());
        Assert.Equal(original, Handler());
    }
}

internal sealed class FakeShortcuts : IShortcutStore
{
    public Dictionary<string, ShortcutInfo> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailWrites { get; set; }
    public ShortcutInfo? Read(string path) => Files.GetValueOrDefault(path);
    public void Write(string path, ShortcutInfo info)
    {
        if (FailWrites) throw new IOException("locked");
        Files[path] = info;
    }
}

internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
}
