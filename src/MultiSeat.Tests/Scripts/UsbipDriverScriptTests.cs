using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiSeat.Tests.Scripts;

/// <summary>
/// The opt-in usbip-win2 driver step (prerequisites\lib\usbip-driver-lib.ps1). USER RULE: the driver
/// is used only while its kernel drivers are Microsoft-signed; otherwise stop. These run the real
/// library in real PowerShell processes (Windows PowerShell 5.1, and 7 when installed) with every side
/// effect (download, signature, installer, driver listing, bcdedit, admin check) replaced by a fake.
/// NOTHING here downloads the real asset or runs an installer, and none of it needs elevation.
/// </summary>
public class UsbipDriverScriptTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Prereq = Path.Combine(RepoRoot, "prerequisites");
    private static readonly string Lib = Path.Combine(Prereq, "lib", "usbip-driver-lib.ps1");
    private static readonly string Wrapper = Path.Combine(Prereq, "install-usbip-driver.ps1");
    private static readonly string Main = Path.Combine(Prereq, "install-prerequisites.ps1");

    private const string InstallerName = "USBip-0.9.8.1-x64.exe";
    private const string MsSigner = "Microsoft Windows Hardware Compatibility Publisher";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "install-service.ps1")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    public static IEnumerable<object[]> Engines()
    {
        yield return new object[] { "powershell.exe" };
        var pwsh = FindPwsh();
        if (pwsh != null) yield return new object[] { pwsh };
    }

    private static string? FindPwsh()
    {
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var f = Path.Combine(p.Trim('"'), "pwsh.exe");
                if (File.Exists(f)) return f;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private sealed record PsResult(int ExitCode, string Stdout, string Stderr, JsonElement? Json);

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private static PsResult RunScript(string engine, string script)
    {
        var psi = new ProcessStartInfo(engine)
        {
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "PowerShell did not finish in 120 s");
        var stdout = outp.Result;
        var line = stdout.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("@@JSON@@"));
        JsonElement? json = line == null ? null : JsonDocument.Parse(line.Substring(8)).RootElement.Clone();
        return new PsResult(p.ExitCode, stdout, err.Result, json);
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ms-usbip-test-").FullName;
        public string Payload { get; }
        public string PayloadHash { get; }
        public Scenario()
        {
            Payload = Path.Combine(Root, "fake-installer.bin");
            File.WriteAllBytes(Payload, Encoding.ASCII.GetBytes("this is a test file, not an installer"));
            PayloadHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Payload)));
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private const string MsRoot = "3B1EFD3A66EA28B16697394703A72CA340A05BD5";

    // Fake world. $overrides runs after the defaults and may change $pin, $sigs, $drivers, $seams, $st.
    private static PsResult Run(string engine, Scenario s, string overrides = "", bool dryRun = false)
    {
        var script = @"
$ErrorActionPreference = 'Stop'
. " + Q(Lib) + @"
$calls = New-Object System.Collections.ArrayList
$st = @{ Installed = $false; Downloaded = $null; Stage = $null }
$pin = @{
  Version='0.9.8.1'; Tag='v.0.9.8.1'; Asset='" + InstallerName + @"'
  Url='https://example.invalid/" + InstallerName + @"'
  Sha256='" + s.PayloadHash + @"'
  InstallerSigner='Test Installer Signer'; DriverSigner='" + MsSigner + @"'
  DriverRootThumbprint='" + MsRoot + @"'; DriverEkus=@('1.3.6.1.4.1.311.10.3.5')
}
$good = @{ Status='Valid'; SimpleName='" + MsSigner + @"'; RootThumbprint='" + MsRoot + @"'; ChainOk=$true; ChainStatus=@(); Ekus=@('1.3.6.1.4.1.311.10.3.5','1.3.6.1.5.5.7.3.3') }
$sigs = @{
  '" + InstallerName + @"' = @{ Status='Valid'; SimpleName='Test Installer Signer'; RootThumbprint='AA'; ChainOk=$true; ChainStatus=@(); Ekus=@() }
  'usbip2_ude.sys' = $good; 'usbip2_filter.sys' = $good; 'usbip2_ude.cat' = $good; 'usbip2_filter.cat' = $good
}
$drivers = @('X:\fake\usbip2_ude.sys','X:\fake\usbip2_filter.sys','X:\fake\usbip2_ude.cat','X:\fake\usbip2_filter.cat')
function Try-Swap($p, $tag) {
  try { [IO.File]::WriteAllText($p, 'evil'); $calls.Add(""swap-write:${tag}:swapped"") | Out-Null } catch { $calls.Add(""swap-write:${tag}:blocked"") | Out-Null }
  try { [IO.File]::Delete($p); $calls.Add(""swap-delete:${tag}:swapped"") | Out-Null } catch { $calls.Add(""swap-delete:${tag}:blocked"") | Out-Null }
}
$seams = @{
  IsAdmin = { $true }
  ReadStartOptions = { ' NOEXECUTE=OPTIN  HYPERVISORLAUNCHTYPE=AUTO' }
  ReadBcd = { $calls.Add('bcdedit') | Out-Null; ""testsigning             No`r`n"" }
  GetInstalledState = {
    $calls.Add('state') | Out-Null
    if ($st.Installed) { @{ Version='0.9.8.1'; DriverFiles=$drivers } } else { @{ Version=$null; DriverFiles=@() } }
  }
  NewStagingDir = { $d = Join-Path ([IO.Path]::GetTempPath()) ('ms-usbip-stage-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $d | Out-Null; $st.Stage = $d; $d }
  Download = { param($Url,$Dest) $calls.Add('download') | Out-Null; $st.Downloaded = $Dest; Copy-Item -LiteralPath " + Q(s.Payload) + @" -Destination $Dest }
  GetSignature = { param($Path) $calls.Add('sig') | Out-Null; $k = Split-Path -Leaf $Path; if ($k -eq '" + InstallerName + @"') { Try-Swap $Path 'sig' }; if ($sigs.ContainsKey($k)) { $sigs[$k] } else { @{ Status='NotSigned'; SimpleName=$null } } }
  RunInstaller = { param($Path,$ArgList,$TimeoutSec) $calls.Add('install:' + ($ArgList -join ' ')) | Out-Null; Try-Swap $Path 'install'; $st.Installed = $true; 0 }
}
" + overrides + @"
$say = { param($m,$c) }
$res = Invoke-UsbipDriverInstall -Pin $pin -Seams $seams " + (dryRun ? "-DryRun " : "") + @"-Say $say
$leftover = $false
if ($st.Downloaded) { $leftover = (Test-Path -LiteralPath $st.Downloaded) }
if ($st.Stage) { $leftover = $leftover -or (Test-Path -LiteralPath $st.Stage) }
$r = @{ ExitCode = $res.ExitCode; Status = $res.Status; DoNotReboot = $res.DoNotReboot; Messages = @($res.Messages); Calls = @($calls); Leftover = $leftover }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress -Depth 5))
";
        return RunScript(engine, script);
    }

    private static JsonElement J(PsResult r)
    {
        Assert.True(r.Json.HasValue, $"no JSON. exit={r.ExitCode}\nstdout:\n{r.Stdout}\nstderr:\n{r.Stderr}");
        return r.Json!.Value;
    }

    private static string[] Calls(JsonElement j) => j.GetProperty("Calls").EnumerateArray().Select(x => x.GetString()!).ToArray();
    private static string Msgs(JsonElement j) => string.Join("\n", j.GetProperty("Messages").EnumerateArray().Select(x => x.GetString()));

    // -- Dry run and the default path ------------------------------------------------

    [Theory, MemberData(nameof(Engines))]
    public void DryRun_PrintsEveryStep_AndTouchesNothing(string engine)
    {
        using var s = new Scenario();
        // Every seam throws: a dry run that called any of them would fail the script.
        var boom = string.Join("\n", new[] { "IsAdmin", "ReadStartOptions", "ReadBcd", "GetInstalledState", "Download", "GetSignature", "RunInstaller", "NewStagingDir" }
            .Select(k => $"$seams['{k}'] = {{ throw 'dry run touched {k}' }}"));
        var j = J(Run(engine, s, boom, dryRun: true));
        Assert.Equal(0, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("DryRun", j.GetProperty("Status").GetString());
        Assert.Empty(Calls(j));
        var m = Msgs(j);
        foreach (var needle in new[] { "DRY RUN", "test-signing", "private staging folder", "SHA-256", "Authenticode", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", "REBOOT", "never reboots" })
            Assert.Contains(needle, m);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Wrapper_WhatIf_PrintsStepsAndExitsZero_WithoutElevation(string engine)
    {
        var r = RunScript(engine, "& " + Q(Wrapper) + " -WhatIf; Write-Output ('EXIT=' + $LASTEXITCODE)");
        Assert.Contains("DRY RUN", r.Stdout);
        Assert.Contains("EXIT=0", r.Stdout);
        Assert.DoesNotContain("Downloading", r.Stdout);
    }

    [Theory, MemberData(nameof(Engines))]
    public void DefaultRun_NeverReachesTheUsbStep(string engine)
    {
        // install-prerequisites.ps1 needs admin and installs real things, so it cannot be run here.
        // Prove it structurally instead: the switch is a plain [switch] with no default, and every
        // command that mentions usbip sits inside `if ($WithUsbForwarding)`.
        var script = @"
$errs = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(" + Q(Main) + @", [ref]$null, [ref]$errs)
$p = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'WithUsbForwarding' }
$isSwitch = [bool]($p -and $p.StaticType -eq [switch] -and $null -eq $p.DefaultValue)
$cmds = $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] -and $a.Extent.Text -match '(?i)usbip' }, $true)
$unguarded = @()
foreach ($c in $cmds) {
  $guarded = $false
  $n = $c.Parent
  while ($n) {
    if ($n -is [System.Management.Automation.Language.IfStatementAst]) {
      foreach ($cl in $n.Clauses) { if ($cl.Item1.Extent.Text.Trim() -eq '$WithUsbForwarding') { $guarded = $true } }
    }
    $n = $n.Parent
  }
  if (-not $guarded) { $unguarded += $c.Extent.Text }
}
$r = @{ IsSwitch = $isSwitch; Count = @($cmds).Count; Unguarded = @($unguarded) }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))
";
        var j = J(RunScript(engine, script));
        Assert.True(j.GetProperty("IsSwitch").GetBoolean(), "-WithUsbForwarding must be a [switch] with no default");
        Assert.True(j.GetProperty("Count").GetInt32() >= 2, "expected the dot-source and the call to be found");
        Assert.Empty(j.GetProperty("Unguarded").EnumerateArray());
    }

    // -- The guarded steps -----------------------------------------------------------

    [Theory, MemberData(nameof(Engines))]
    public void HappyPath_InstallsSilentlyWithNoRestart_ThenVerifiesDrivers_AndSaysRebootIsYours(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s));
        Assert.Equal(0, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("Installed", j.GetProperty("Status").GetString());
        Assert.False(j.GetProperty("DoNotReboot").GetBoolean());
        var calls = Calls(j);
        Assert.Contains(calls, c => c.StartsWith("install:/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="));
        Assert.True(Array.IndexOf(calls, "bcdedit") < Array.IndexOf(calls, "download"));
        Assert.True(Array.IndexOf(calls, "download") < Array.FindIndex(calls, c => c.StartsWith("install:")));
        var m = Msgs(j);
        Assert.Contains("REBOOT is required", m);
        Assert.Contains("your decision", m);
        Assert.False(j.GetProperty("Leftover").GetBoolean(), "the staging folder must be cleaned up");
    }

    [Theory, MemberData(nameof(Engines))]
    public void VerifiedFile_CannotBeSwappedWhileVerifyingOrInstalling(string engine)
    {
        // H1: the file is held open (read share only) from the hash check until the installer returns.
        // The fakes try to overwrite and delete it during the signature check and during the install.
        using var s = new Scenario();
        var j = J(Run(engine, s));
        var swaps = Calls(j).Where(c => c.StartsWith("swap-")).ToArray();
        Assert.Equal(4, swaps.Length);
        Assert.All(swaps, c => Assert.EndsWith(":blocked", c));
    }

    [Theory, MemberData(nameof(Engines))]
    public void WrongHash_IsRefused_AndTheFileIsRemoved(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$pin.Sha256 = '" + new string('0', 64) + "'"));
        Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("HashMismatch", j.GetProperty("Status").GetString());
        Assert.DoesNotContain(Calls(j), c => c.StartsWith("install:"));
        Assert.False(j.GetProperty("Leftover").GetBoolean());
    }

    [Theory, MemberData(nameof(Engines))]
    public void UnsignedInstaller_IsRefused(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$sigs['" + InstallerName + "'] = @{ Status='NotSigned'; SimpleName=$null }"));
        Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("InstallerSignature", j.GetProperty("Status").GetString());
        Assert.DoesNotContain(Calls(j), c => c.StartsWith("install:"));
        Assert.False(j.GetProperty("Leftover").GetBoolean());
    }

    [Theory, MemberData(nameof(Engines))]
    public void TamperedSignature_StatusHashMismatch_IsRefused(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$sigs['" + InstallerName + "'] = @{ Status='HashMismatch'; SimpleName='Test Installer Signer' }"));
        Assert.Equal("InstallerSignature", j.GetProperty("Status").GetString());
        Assert.DoesNotContain(Calls(j), c => c.StartsWith("install:"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void WrongInstallerSigner_IsRefused(string engine)
    {
        using var s = new Scenario();
        // A name that merely CONTAINS the right one, and a different case, are both wrong.
        foreach (var bad in new[] { "Someone Else", "Test Installer Signer Ltd", "test installer signer" })
        {
            var j = J(Run(engine, s, "$sigs['" + InstallerName + "'] = @{ Status='Valid'; SimpleName='" + bad + "' }"));
            Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
            Assert.Equal("InstallerSignature", j.GetProperty("Status").GetString());
            Assert.DoesNotContain(Calls(j), c => c.StartsWith("install:"));
        }
    }

    // -- Test-signing / integrity checks (M2) -----------------------------------------

    [Theory, MemberData(nameof(Engines))]
    public void TestSigning_InRunningBootOptions_IsRefused_BeforeAnyDownload(string engine)
    {
        using var s = new Scenario();
        foreach (var opt in new[] { " NOEXECUTE=OPTIN TESTSIGNING", " DISABLE_INTEGRITY_CHECKS", "NOINTEGRITYCHECKS" })
        {
            var j = J(Run(engine, s, "$seams['ReadStartOptions'] = { '" + opt + "' }"));
            Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
            Assert.Equal("TestSigning", j.GetProperty("Status").GetString());
            var calls = Calls(j);
            Assert.DoesNotContain("download", calls);
            Assert.DoesNotContain(calls, c => c.StartsWith("install:"));
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public void TestSigning_InStoredBcd_IsRefused_EvenWhenTheRunningBootLooksClean(string engine)
    {
        using var s = new Scenario();
        foreach (var bcd in new[] { "testsigning             Yes", "nointegritychecks        Yes", "testsigning             Ja" })
        {
            var j = J(Run(engine, s, "$seams['ReadBcd'] = { '" + bcd + "' }"));
            Assert.Equal("TestSigning", j.GetProperty("Status").GetString());
            Assert.DoesNotContain("download", Calls(j));
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public void BootState_Unknown_IsRefused(string engine)
    {
        using var s = new Scenario();
        // Neither source readable -> stop. Either an empty answer or an exception counts as unreadable.
        var j = J(Run(engine, s, "$seams['ReadStartOptions'] = { $null }; $seams['ReadBcd'] = { throw 'no bcdedit' }"));
        Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("TestSigning", j.GetProperty("Status").GetString());
        Assert.Contains("cannot determine", Msgs(j));
        Assert.DoesNotContain("download", Calls(j));
    }

    [Theory, MemberData(nameof(Engines))]
    public void BootState_OneSourceReadable_IsEnough(string engine)
    {
        using var s = new Scenario();
        // bcdedit missing/localized garbage, but the running boot options (language independent) are clean.
        var j = J(Run(engine, s, "$seams['ReadBcd'] = { $null }"));
        Assert.Equal(0, j.GetProperty("ExitCode").GetInt32());
    }

    [Theory, MemberData(nameof(Engines))]
    public void RealRegistryRead_WorksWithoutElevation(string engine)
    {
        var script = ". " + Q(Lib) + @"
$exists = [bool](Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name SystemStartOptions -ErrorAction SilentlyContinue)
$ro = (New-UsbipDefaultSeams).ReadStartOptions
$v = & $ro
$verdict = Get-UsbipBootVerdict @{ ReadStartOptions = $ro; ReadBcd = { $null } }
$r = @{ Exists = $exists; IsString = ($v -is [string]); Refuse = $verdict.Refuse; Reason = $verdict.Reason }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))";
        var j = J(RunScript(engine, script));
        if (!j.GetProperty("Exists").GetBoolean()) return;   // a host without the value: nothing to prove
        Assert.True(j.GetProperty("IsString").GetBoolean());
        // Whether the verdict is "refuse" depends on the machine: a CI runner can be in test-signing mode, a
        // normal host is not. So compare the script's real read with an independent read made here, from the
        // same registry value, and require the verdict to agree on either kind of host.
        var raw = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "SystemStartOptions", null) as string;
        Assert.NotNull(raw);
        var expectRefuse = System.Text.RegularExpressions.Regex.IsMatch(raw,
            @"(?i)(^|[\s,])(TESTSIGNING|DISABLE_INTEGRITY_CHECKS|NOINTEGRITYCHECKS)(?=$|[\s,=])");
        Assert.True(expectRefuse == j.GetProperty("Refuse").GetBoolean(),
            $"verdict disagrees with the real start options (expected refuse={expectRefuse}): {j.GetProperty("Reason").GetString()}");
    }

    [Theory, MemberData(nameof(Engines))]
    public void NotElevated_IsRefused(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$seams['IsAdmin'] = { $false }"));
        Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
        Assert.Empty(Calls(j));
    }

    // -- Installer outcomes (L1) -------------------------------------------------------

    [Theory, MemberData(nameof(Engines))]
    public void InstallerFailure_IsReported_NotClaimedAsSuccess(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$seams['RunInstaller'] = { param($P,$A,$T) $calls.Add('install:x') | Out-Null; 5 }"));
        Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("InstallerFailed", j.GetProperty("Status").GetString());
        Assert.DoesNotContain("REBOOT is required", Msgs(j));
    }

    [Theory, MemberData(nameof(Engines))]
    public void InstallerTimeout_IsReported_AndNothingIsClaimed(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$seams['RunInstaller'] = { param($P,$A,$T) $calls.Add('install:timeout=' + $T) | Out-Null; $null }"));
        Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("InstallerTimeout", j.GetProperty("Status").GetString());
        Assert.Contains("install:timeout=600", Calls(j));
        Assert.Contains("not killed", Msgs(j));
    }

    [Theory, MemberData(nameof(Engines))]
    public void InnoExitCode8_ExplainsARestartIsNeededFirst_WithoutRestarting(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$seams['RunInstaller'] = { param($P,$A,$T) 8 }"));
        Assert.Equal(1, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("InstallerCannotProceed", j.GetProperty("Status").GetString());
        Assert.Contains("restart is needed first", Msgs(j));
        Assert.Contains("will not restart anything", Msgs(j));
    }

    // -- The user rule: installed drivers must be Microsoft-signed ----------------------

    [Theory, MemberData(nameof(Engines))]
    public void InstalledDriver_NotMicrosoftSigned_StopsWithExit2_AndSaysItIsInstalled(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$sigs['usbip2_filter.sys'] = @{ Status='Valid'; SimpleName='Some Test Cert'; RootThumbprint='" + MsRoot + "'; ChainOk=$true; Ekus=@('1.3.6.1.4.1.311.10.3.5') }"));
        Assert.Equal(2, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("InstalledDriverNotMicrosoftSigned", j.GetProperty("Status").GetString());
        Assert.True(j.GetProperty("DoNotReboot").GetBoolean());
        var m = Msgs(j);
        Assert.Contains("STOP", m);
        Assert.Contains("usbip2_filter.sys", m);
        Assert.Contains("ALREADY INSTALLED", m);
        Assert.Contains("LOAD at the next boot", m);
        Assert.Contains("Do NOT reboot until it is removed or verified", m);
        Assert.Contains("pnputil /delete-driver oemNN.inf /uninstall /force", m);
        Assert.Contains("unins000.exe", m);
        Assert.Contains("NOT run by this script", m);
        Assert.DoesNotContain("REBOOT is required", m);
        // Printed, never executed: the only calls are the normal ones, no removal.
        Assert.DoesNotContain(Calls(j), c => c.Contains("delete-driver") || c.Contains("unins"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void InstalledDriver_Unsigned_StopsWithExit2(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$sigs['usbip2_ude.sys'] = @{ Status='NotSigned'; SimpleName=$null }"));
        Assert.Equal(2, j.GetProperty("ExitCode").GetInt32());
    }

    [Theory, MemberData(nameof(Engines))]
    public void InstalledDriver_RightNameButWrongRoot_IsRejected(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$sigs['usbip2_ude.sys'] = @{ Status='Valid'; SimpleName='" + MsSigner + "'; RootThumbprint='" + new string('A', 40) + "'; ChainOk=$true; Ekus=@('1.3.6.1.4.1.311.10.3.5') }"));
        Assert.Equal(2, j.GetProperty("ExitCode").GetInt32());
        Assert.Contains("not Microsoft Root Certificate Authority 2010", Msgs(j));
    }

    [Theory, MemberData(nameof(Engines))]
    public void InstalledDriver_ChainNotOk_Or_NoHardwareEku_IsRejected(string engine)
    {
        using var s = new Scenario();
        var a = J(Run(engine, s, "$sigs['usbip2_ude.sys'] = @{ Status='Valid'; SimpleName='" + MsSigner + "'; RootThumbprint='" + MsRoot + "'; ChainOk=$false; ChainStatus=@('Revoked'); Ekus=@('1.3.6.1.4.1.311.10.3.5') }"));
        Assert.Equal(2, a.GetProperty("ExitCode").GetInt32());
        Assert.Contains("chain is not acceptable", Msgs(a));
        var b = J(Run(engine, s, "$sigs['usbip2_ude.sys'] = @{ Status='Valid'; SimpleName='" + MsSigner + "'; RootThumbprint='" + MsRoot + "'; ChainOk=$true; Ekus=@('1.3.6.1.5.5.7.3.3') }"));
        Assert.Equal(2, b.GetProperty("ExitCode").GetInt32());
        Assert.Contains("no Windows hardware driver EKU", Msgs(b));
    }

    [Theory, MemberData(nameof(Engines))]
    public void NoDriverFilesFound_FailsClosed(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$drivers = @()"));
        Assert.Equal(2, j.GetProperty("ExitCode").GetInt32());
        Assert.Contains("nothing could be verified", Msgs(j));
    }

    [Theory, MemberData(nameof(Engines))]
    public void DriverEnumerationThrowing_AfterInstall_IsExit2_WithTheInstalledNotice(string engine)
    {
        // M3: a DISM failure after the installer ran must not become a bare exit 1 with no warning.
        using var s = new Scenario();
        var j = J(Run(engine, s, @"$seams['GetInstalledState'] = { if ($st.Installed) { throw 'DISM exploded' } else { @{ Version=$null; DriverFiles=@() } } }"));
        Assert.Equal(2, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("InstalledDriverNotMicrosoftSigned", j.GetProperty("Status").GetString());
        Assert.True(j.GetProperty("DoNotReboot").GetBoolean());
        var m = Msgs(j);
        Assert.Contains("DISM exploded", m);
        Assert.Contains("ALREADY INSTALLED", m);
        Assert.Contains("Do NOT reboot", m);
        Assert.Contains("pnputil /delete-driver", m);
    }

    [Theory, MemberData(nameof(Engines))]
    public void AlreadyInstalledAtPin_Skips_ButStillChecksTheDrivers(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$st.Installed = $true"));
        Assert.Equal(0, j.GetProperty("ExitCode").GetInt32());
        Assert.Equal("AlreadyInstalled", j.GetProperty("Status").GetString());
        Assert.DoesNotContain("download", Calls(j));
        Assert.Contains("already installed", Msgs(j));
    }

    [Theory, MemberData(nameof(Engines))]
    public void AlreadyInstalled_WithNonMicrosoftDriver_StopsWithExit2(string engine)
    {
        using var s = new Scenario();
        var j = J(Run(engine, s, "$st.Installed = $true; $sigs['usbip2_ude.cat'] = @{ Status='Valid'; SimpleName='Test' }"));
        Assert.Equal(2, j.GetProperty("ExitCode").GetInt32());
        Assert.DoesNotContain("download", Calls(j));
    }

    // -- H2: the reboot prompt must not appear when the driver is untrusted -------------------

    [Theory, MemberData(nameof(Engines))]
    public void RebootPrompt_IsNeverOffered_WhenDriverUntrusted_EvenIfAnotherStepNeedsAReboot(string engine)
    {
        // Run the REAL summary block from install-prerequisites.ps1 (found by AST) with the reboot
        // flag already set and exit code 2, with Read-Host and Restart-Computer replaced by recorders.
        var script = @"
$errs = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(" + Q(Main) + @", [ref]$null, [ref]$errs)
$blocks = $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text.Trim() -eq '$UsbipExit -eq 2' }, $true)
$text = @($blocks)[0].Extent.Text
$global:prompted = $false; $global:rebooted = $false
function Read-Host { $global:prompted = $true; 'y' }
function Restart-Computer { $global:rebooted = $true }
function Run-It($exit, $needs, $skip) {
  $global:prompted = $false; $global:rebooted = $false
  $UsbipExit = $exit; $NeedsReboot = $needs; $SkipReboot = $skip
  $out = & { Invoke-Expression $text } 6>&1 | Out-String
  @{ Prompted = $global:prompted; Rebooted = $global:rebooted; Out = $out }
}
$bad = Run-It 2 $true $false
$ok = Run-It 0 $true $false
$r = @{ Found = (@($blocks).Count -eq 1); BadPrompted = $bad.Prompted; BadRebooted = $bad.Rebooted; BadOut = $bad.Out; OkPrompted = $ok.Prompted }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))";
        var j = J(RunScript(engine, script));
        Assert.True(j.GetProperty("Found").GetBoolean());
        Assert.False(j.GetProperty("BadPrompted").GetBoolean(), "the prompt was offered with an untrusted driver");
        Assert.False(j.GetProperty("BadRebooted").GetBoolean());
        Assert.Contains("DO NOT REBOOT", j.GetProperty("BadOut").GetString());
        // Control: the original behaviour is intact for every other case, so the test above is not vacuous.
        Assert.True(j.GetProperty("OkPrompted").GetBoolean());
    }

    // -- Staging folder and held file (H1) ----------------------------------------------------

    [Theory, MemberData(nameof(Engines))]
    public void StagingDir_IsPrivate_ToSystemAndAdministratorsOnly(string engine)
    {
        var root = Directory.CreateTempSubdirectory("ms-usbip-root-").FullName;
        try
        {
            var script = ". " + Q(Lib) + @"
$root = " + Q(root) + @"
$dir = New-UsbipStagingDir -Root $root
$sec = Get-UsbipDirSecurity $dir
$allow = @($sec.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object { $_.AccessControlType -eq 'Allow' } | ForEach-Object { $_.IdentityReference.Value })
$elevated = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$canWrite = $true
try { [IO.File]::WriteAllText((Join-Path $dir 'probe.txt'), 'x') } catch { $canWrite = $false }
$r = @{ Protected = $sec.AreAccessRulesProtected; Allow = @($allow); Problems = @(Get-UsbipStagingAclProblems $dir); Elevated = $elevated; CanWrite = $canWrite; UnderRoot = $dir.StartsWith($root) }
# the owner can always reset the DACL, so cleanup works without elevation
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User
$fix = Get-UsbipDirSecurity $dir
$fix.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($me,'FullControl','ContainerInherit, ObjectInherit','None','Allow')))
Set-UsbipDirSecurity $dir $fix
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))";
            var j = J(RunScript(engine, script));
            Assert.True(j.GetProperty("UnderRoot").GetBoolean());
            Assert.True(j.GetProperty("Protected").GetBoolean(), "the DACL must not inherit");
            var allow = j.GetProperty("Allow").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }, allow);   // SYSTEM, Administrators: nobody else
            Assert.Empty(j.GetProperty("Problems").EnumerateArray());
            // A standard (non-elevated) account cannot write there. Only checkable from a non-elevated shell.
            if (!j.GetProperty("Elevated").GetBoolean())
                Assert.False(j.GetProperty("CanWrite").GetBoolean(), "a standard user could write into the staging folder");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Theory, MemberData(nameof(Engines))]
    public void StagingRoot_IsMachineWide_NotTheUserProfileOrTemp(string engine)
    {
        var script = ". " + Q(Lib) + @"
$r = @{ Root = (Get-UsbipStagingRoot); ProgramData = $env:ProgramData; Temp = [IO.Path]::GetTempPath(); Profile = $env:USERPROFILE }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))";
        var j = J(RunScript(engine, script));
        var root = j.GetProperty("Root").GetString()!;
        Assert.Equal(j.GetProperty("ProgramData").GetString(), root);
        Assert.False(root.StartsWith(j.GetProperty("Profile").GetString()!, StringComparison.OrdinalIgnoreCase));
        Assert.False(j.GetProperty("Temp").GetString()!.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    [Theory, MemberData(nameof(Engines))]
    public void HeldFile_CannotBeWrittenDeletedOrMoved_ButCanBeRead(string engine)
    {
        using var s = new Scenario();
        var script = ". " + Q(Lib) + @"
$f = Join-Path " + Q(s.Root) + @" 'held.bin'
[IO.File]::WriteAllText($f, 'original')
$h = Open-UsbipHeldFile $f
$o = @{}
try { [IO.File]::WriteAllText($f, 'evil'); $o.Write = 'swapped' } catch { $o.Write = 'blocked' }
try { [IO.File]::Delete($f); $o.Delete = 'swapped' } catch { $o.Delete = 'blocked' }
try { [IO.File]::Move($f, $f + '.moved'); $o.Move = 'swapped' } catch { $o.Move = 'blocked' }
try { $o.Read = [IO.File]::ReadAllText($f) } catch { $o.Read = 'denied' }
$o.Hash = Get-UsbipStreamSha256 $h
$h.Dispose()
try { [IO.File]::Delete($f); $o.AfterClose = 'deleted' } catch { $o.AfterClose = 'still locked' }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $o -Compress))";
        var j = J(RunScript(engine, script));
        Assert.Equal("blocked", j.GetProperty("Write").GetString());
        Assert.Equal("blocked", j.GetProperty("Delete").GetString());
        Assert.Equal("blocked", j.GetProperty("Move").GetString());
        Assert.Equal("original", j.GetProperty("Read").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("original"))), j.GetProperty("Hash").GetString());
        Assert.Equal("deleted", j.GetProperty("AfterClose").GetString());
    }

    // -- M1: signer identity from the certificate object, never a parsed string -------------------

    private static string CertScript(string subject) => ". " + Q(Lib) + @"
Add-Type -AssemblyName System.Security
$dn = New-Object System.Security.Cryptography.X509Certificates.X500DistinguishedName(" + Q(subject) + @")
$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest($dn, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
$facts = Get-UsbipCertFacts $cert
";

    [Theory, MemberData(nameof(Engines))]
    public void SpoofedSubject_IsNotReadAsTheMicrosoftName(string engine)
    {
        // The O value is quoted and contains ', CN=<Microsoft name>'. A regex over the subject string
        // reads that as the CN; the certificate's SimpleName is the real CN.
        var spoof = "O=\"Evil, CN=Microsoft Windows Hardware Compatibility Publisher, x\", CN=Evil Ltd";
        var j = J(RunScript(engine, CertScript(spoof) + @"
$r = @{ Simple = $facts.SimpleName; Subject = $cert.Subject }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))"));
        Assert.Equal("Evil Ltd", j.GetProperty("Simple").GetString());
    }

    [Theory, MemberData(nameof(Engines))]
    public void RealSubjects_GiveTheExpectedNames(string engine)
    {
        var installer = "E=itsec@scheibling.se, CN=Cloudyne Systems (Scheibling Consulting AB), O=Cloudyne Systems (Scheibling Consulting AB), STREET=Fyrislundsgatan 29 a, L=Uppsala, S=Uppsala, C=SE, OID.1.3.6.1.4.1.311.60.2.1.3=SE, SERIALNUMBER=559171-6567, OID.2.5.4.15=Private Organization";
        var ms = "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var a = J(RunScript(engine, CertScript(installer) + @"
$r = @{ Simple = $facts.SimpleName; Pinned = $script:UsbipPin.InstallerSigner }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))"));
        Assert.Equal("Cloudyne Systems (Scheibling Consulting AB)", a.GetProperty("Simple").GetString());
        Assert.Equal(a.GetProperty("Pinned").GetString(), a.GetProperty("Simple").GetString());
        var b = J(RunScript(engine, CertScript(ms) + @"
$r = @{ Simple = $facts.SimpleName }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))"));
        Assert.Equal(MsSigner, b.GetProperty("Simple").GetString());
    }

    [Theory, MemberData(nameof(Engines))]
    public void SelfSignedCert_WithTheExactMicrosoftName_IsStillRejectedAsADriverSigner(string engine)
    {
        var ms = "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var j = J(RunScript(engine, CertScript(ms) + @"
$facts.Status = 'Valid'
$seams = @{ GetSignature = { param($p) $facts } }
$why = ''
$ok = Test-UsbipSignedBy 'X:\fake\usbip2_ude.sys' $script:UsbipPin 'Driver' $seams ([ref]$why)
$r = @{ Ok = $ok; Why = $why; Root = $facts.RootThumbprint; ChainOk = $facts.ChainOk }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))"));
        Assert.False(j.GetProperty("Ok").GetBoolean());
        Assert.False(j.GetProperty("ChainOk").GetBoolean());
    }

    // Real files already on this machine. The positive case is a real Microsoft hardware-compatibility
    // (WHQL) signed driver; negatives are unsigned and a real non-Microsoft signed file.
    private const string RealFilesScript = @"
$gs = (New-UsbipDefaultSeams).GetSignature
function Check($path) {
  $why = ''
  $ok = Test-UsbipSignedBy $path $script:UsbipPin 'Driver' @{ GetSignature = $gs } ([ref]$why)
  @{ Path = $path; Ok = $ok; Why = $why }
}
$drivers = @(Get-ChildItem $env:windir\System32\drivers -Filter *.sys -File -ErrorAction SilentlyContinue)
";

    [Theory, MemberData(nameof(Engines))]
    public void RealMicrosoftSignedDriver_IsAccepted_ByTheDefaultCheck(string engine)
    {
        var script = ". " + Q(Lib) + RealFilesScript + @"
$pick = $null
foreach ($d in $drivers) {
  $s = Get-AuthenticodeSignature -LiteralPath $d.FullName
  if ($s.Status -eq 'Valid' -and $s.SignerCertificate -and $s.SignerCertificate.GetNameInfo('SimpleName',$false) -eq $script:UsbipPin.DriverSigner) { $pick = $d.FullName; break }
}
if (-not $pick) { Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject @{ Skipped = $true } -Compress)); return }
$r = Check $pick
$r.Skipped = $false
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress))";
        var j = J(RunScript(engine, script));
        if (j.GetProperty("Skipped").GetBoolean()) return;   // no WHQL driver on this host: nothing to prove
        Assert.True(j.GetProperty("Ok").GetBoolean(), j.GetProperty("Path").GetString() + ": " + j.GetProperty("Why").GetString());
    }

    [Theory, MemberData(nameof(Engines))]
    public void RealNegatives_UnsignedFile_AndMicrosoftWindowsComponent_AndThirdParty_AreRejected(string engine)
    {
        using var s = new Scenario();
        var script = ". " + Q(Lib) + RealFilesScript + @"
$res = @{}
$res.Unsigned = Check " + Q(s.Payload) + @"
# A Microsoft-signed Windows component is signed as 'Microsoft Windows', not the hardware publisher.
$comp = $null
foreach ($d in $drivers | Select-Object -First 300) {
  $sg = Get-AuthenticodeSignature -LiteralPath $d.FullName
  if ($sg.Status -eq 'Valid' -and $sg.SignerCertificate -and $sg.SignerCertificate.GetNameInfo('SimpleName',$false) -eq 'Microsoft Windows') { $comp = $d.FullName; break }
}
if ($comp) { $res.Component = Check $comp }
# A real Valid signature from someone who is not Microsoft.
$third = $null
$cands = @($drivers | Select-Object -First 400) + @(Get-ChildItem $env:ProgramFiles -Recurse -Depth 2 -Include *.exe,*.dll -File -ErrorAction SilentlyContinue | Select-Object -First 400)
foreach ($d in $cands) {
  $sg = Get-AuthenticodeSignature -LiteralPath $d.FullName
  if ($sg.Status -eq 'Valid' -and $sg.SignerCertificate -and $sg.SignerCertificate.GetNameInfo('SimpleName',$false) -notmatch 'Microsoft') { $third = $d.FullName; break }
}
if ($third) { $res.Third = Check $third }
Write-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $res -Compress -Depth 4))";
        var j = J(RunScript(engine, script));
        Assert.False(j.GetProperty("Unsigned").GetProperty("Ok").GetBoolean());
        if (j.TryGetProperty("Component", out var c)) Assert.False(c.GetProperty("Ok").GetBoolean(), c.GetProperty("Path").GetString());
        if (j.TryGetProperty("Third", out var t)) Assert.False(t.GetProperty("Ok").GetBoolean(), t.GetProperty("Path").GetString());
    }

    // -- Static guards ---------------------------------------------------------------------

    [Fact]
    public void NewScripts_ContainNoRebootOrShutdownOrTestSigningChange()
    {
        // The existing install-prerequisites.ps1 has a user-answered reboot prompt for OTHER steps;
        // the new files, and the USB step block inside it, must not be able to restart the machine.
        var forbidden = new Regex(@"(?i)\b(Restart-Computer|Stop-Computer|shutdown(\.exe)?|Restart-Service|bcdedit(\.exe)?\s+/set|testsigning\s+on)\b");
        foreach (var f in new[] { Lib, Wrapper })
        {
            // Comments may mention the words; strip them before looking.
            var code = string.Join("\n", File.ReadAllLines(f).Select(l => Regex.Replace(l, @"^\s*#.*$", "")));
            code = Regex.Replace(code, @"(?m)#[^'""\r\n]*$", "");
            Assert.False(forbidden.IsMatch(code), $"{Path.GetFileName(f)} contains a reboot/shutdown/test-signing command");
        }

        var main = File.ReadAllText(Main);
        var start = main.IndexOf("# 10. USB forwarding driver", StringComparison.Ordinal);
        var end = main.IndexOf("# Summary", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        Assert.DoesNotMatch(forbidden, main.Substring(start, end - start));
        Assert.DoesNotMatch(@"\$NeedsReboot\s*=\s*\$true", main.Substring(start, end - start));
    }

    [Fact]
    public void Installer_AlwaysGetsNoRestart_AndPinsAreExact()
    {
        var lib = File.ReadAllText(Lib);
        Assert.Contains("'/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'", lib);
        Assert.Contains("38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A", lib);
        Assert.Contains("https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe", lib);
        Assert.Contains("Microsoft Windows Hardware Compatibility Publisher", lib);
        Assert.Contains(MsRoot, lib);
    }

    [Fact]
    public void SignerNames_AreNeverTakenFromAParsedSubjectString()
    {
        var code = string.Join("\n", File.ReadAllLines(Lib).Select(l => Regex.Replace(l, @"^\s*#.*$", "")));
        Assert.DoesNotMatch(@"\.Subject\b", code);
        Assert.Contains("GetNameInfo", code);
    }

    [Fact]
    public void NewFiles_ContainNoLocalUserPaths()
    {
        foreach (var f in new[] { Lib, Wrapper })
            Assert.DoesNotMatch(@"(?i)[A-Z]:\\Users\\", File.ReadAllText(f));
    }

    [Theory, MemberData(nameof(Engines))]
    public void LintScript_PassesOnThePrerequisitesFolder(string engine)
    {
        var r = RunScript(engine, "& " + Q(Path.Combine(RepoRoot, "scripts", "lint-scripts.ps1")) + " -Path " + Q(Prereq) + "; Write-Output ('EXIT=' + $LASTEXITCODE)");
        Assert.Contains("EXIT=0", r.Stdout + r.Stderr);
    }
}
