using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MultiSeat.Tests.Scripts;

/// <summary>
/// scripts\lib\install-lib.ps1 holds the logic behind install-service.ps1's stage-verify-replace
/// deploy. These tests run it in a real PowerShell process (Windows PowerShell 5.1, which is the
/// shell users get by typing "powershell", and PowerShell 7 when present) against REAL temporary
/// folders. Nothing here touches a real install folder, a service or the registry.
///
/// The failure they guard against (measured 2026-10-08): `dotnet publish` into a live install
/// folder that was self-contained left a framework-dependent runtimeconfig.json beside a leftover
/// hostfxr.dll, and the service would not start ("No frameworks were found").
/// </summary>
public class InstallLibTests
{
    // -- Process plumbing ------------------------------------------------------------

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Lib = Path.Combine(RepoRoot, "scripts", "lib", "install-lib.ps1");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "install-service.ps1")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root with scripts\\install-service.ps1 not found");
    }

    /// <summary>Windows PowerShell 5.1 always; PowerShell 7 too when it is installed.</summary>
    public static IEnumerable<object[]> Engines()
    {
        yield return new object[] { "powershell.exe" };
        var pwsh = FindOnPath("pwsh.exe");
        if (pwsh != null) yield return new object[] { pwsh };
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var f = Path.Combine(p.Trim('"'), exe);
                if (File.Exists(f)) return f;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private sealed record PsResult(int ExitCode, string Stdout, string Stderr, JsonElement? Json);

    /// <summary>Dot-source the library, run <paramref name="body"/>, which must set $r.</summary>
    private static PsResult Ps(string engine, string body)
    {
        var script = "$ErrorActionPreference = 'Stop'\n. " + Q(Lib) + "\n" + body +
                     "\nWrite-Output ('@@JSON@@' + (ConvertTo-Json -InputObject $r -Compress -Depth 6))\n";
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
        JsonElement? json = line == null ? null : JsonDocument.Parse(line.Substring("@@JSON@@".Length)).RootElement.Clone();
        return new PsResult(p.ExitCode, stdout, err.Result, json);
    }

    private static JsonElement J(PsResult r)
    {
        Assert.True(r.Json.HasValue, $"no JSON result. exit={r.ExitCode}\nstdout:\n{r.Stdout}\nstderr:\n{r.Stderr}");
        return r.Json!.Value;
    }

    /// <summary>A PowerShell single-quoted literal.</summary>
    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private static string[] Strings(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!).ToArray()
        : e.ValueKind == JsonValueKind.String ? new[] { e.GetString()! }
        : Array.Empty<string>();

    // -- Fixtures: real folders ------------------------------------------------------

    private const string FrameworkDependentConfig = @"{
  ""runtimeOptions"": {
    ""tfm"": ""net9.0"",
    ""frameworks"": [
      { ""name"": ""Microsoft.NETCore.App"", ""version"": ""9.0.0"" },
      { ""name"": ""Microsoft.AspNetCore.App"", ""version"": ""9.0.0"" }
    ],
    ""configProperties"": { ""System.GC.Server"": true }
  }
}";

    private const string SelfContainedConfig = @"{
  ""runtimeOptions"": {
    ""tfm"": ""net9.0"",
    ""includedFrameworks"": [
      { ""name"": ""Microsoft.NETCore.App"", ""version"": ""9.0.20"" },
      { ""name"": ""Microsoft.AspNetCore.App"", ""version"": ""9.0.20"" }
    ],
    ""configProperties"": { ""System.GC.Server"": true }
  }
}";

    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ms-installlib-").FullName;
        public string Sub(string name) => Path.Combine(Root, name);
        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Put(string dir, string rel, byte[] bytes)
    {
        var f = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllBytes(f, bytes);
    }

    private static void Put(string dir, string rel, string text) => Put(dir, rel, Encoding.UTF8.GetBytes(text));

    private static void CommonFiles(string dir, string tag)
    {
        Put(dir, "MultiSeat.Service.exe", "exe-" + tag);
        Put(dir, "MultiSeat.Service.dll", "dll-" + tag);
        Put(dir, "appsettings.json", "{\"MultiSeat\":{\"A\":1,\"NewSetting\":2}}");
        Put(dir, "wwwroot/index.html", "<html>" + tag + "</html>");
        Put(dir, "wwwroot/assets/app.js", "js-" + tag);
    }

    private static string FrameworkDependent(string dir, string tag = "fd")
    {
        Directory.CreateDirectory(dir);
        CommonFiles(dir, tag);
        Put(dir, "MultiSeat.Service.runtimeconfig.json", FrameworkDependentConfig);
        return dir;
    }

    private static string SelfContained(string dir, string tag = "sc")
    {
        Directory.CreateDirectory(dir);
        CommonFiles(dir, tag);
        Put(dir, "MultiSeat.Service.runtimeconfig.json", SelfContainedConfig);
        foreach (var f in new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll" }) Put(dir, f, "bundled-" + f);
        return dir;
    }

    /// <summary>name -> sha256 for every file under a folder.</summary>
    private static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    // -- Consistency check -----------------------------------------------------------

    private static (bool Ok, string Mode, string[] Problems) Check(string engine, string dir)
    {
        var j = J(Ps(engine, "$r = Test-PayloadConsistency -Dir " + Q(dir)));
        return (j.GetProperty("Ok").GetBoolean(), j.GetProperty("Mode").GetString()!, Strings(j.GetProperty("Problems")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Accepts_a_clean_framework_dependent_payload(string engine)
    {
        using var t = new Temp();
        var (ok, mode, problems) = Check(engine, FrameworkDependent(t.Sub("p")));
        Assert.True(ok, string.Join(" | ", problems));
        Assert.Equal("framework-dependent", mode);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Accepts_a_clean_self_contained_payload(string engine)
    {
        using var t = new Temp();
        var (ok, mode, problems) = Check(engine, SelfContained(t.Sub("p")));
        Assert.True(ok, string.Join(" | ", problems));
        Assert.Equal("self-contained", mode);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Rejects_the_hybrid_that_failed_on_2026_10_08(string engine)
    {
        // Framework-dependent runtimeconfig.json beside a leftover bundled hostfxr.dll.
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        Put(dir, "hostfxr.dll", "leftover");
        var (ok, _, problems) = Check(engine, dir);
        Assert.False(ok);
        var all = string.Join(" ", problems);
        Assert.Contains("hostfxr.dll", all);
        Assert.Contains("No frameworks were found", all);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Rejects_the_inverse_hybrid_self_contained_config_with_no_hostfxr(string engine)
    {
        using var t = new Temp();
        var dir = SelfContained(t.Sub("p"));
        File.Delete(Path.Combine(dir, "hostfxr.dll"));
        var (ok, _, problems) = Check(engine, dir);
        Assert.False(ok);
        Assert.Contains("includedFrameworks", string.Join(" ", problems));
        Assert.Contains("hostfxr.dll is absent", string.Join(" ", problems));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Rejects_a_missing_runtimeconfig(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        File.Delete(Path.Combine(dir, "MultiSeat.Service.runtimeconfig.json"));
        var (ok, _, problems) = Check(engine, dir);
        Assert.False(ok);
        Assert.Contains("runtimeconfig.json is missing", string.Join(" ", problems));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Rejects_a_framework_dependent_payload_with_any_bundled_runtime_file(string engine)
    {
        // No hostfxr.dll, but a stray coreclr.dll: still not a clean framework-dependent folder.
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        Put(dir, "coreclr.dll", "stray");
        var (ok, _, problems) = Check(engine, dir);
        Assert.False(ok);
        Assert.Contains("coreclr.dll", string.Join(" ", problems));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Rejects_a_self_contained_payload_with_an_incomplete_bundled_runtime(string engine)
    {
        using var t = new Temp();
        var dir = SelfContained(t.Sub("p"));
        File.Delete(Path.Combine(dir, "coreclr.dll"));
        var (ok, _, problems) = Check(engine, dir);
        Assert.False(ok);
        Assert.Contains("incomplete", string.Join(" ", problems));
        Assert.Contains("coreclr.dll", string.Join(" ", problems));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Rejects_a_runtimeconfig_that_cannot_be_parsed(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        Put(dir, "MultiSeat.Service.runtimeconfig.json", "{ this is not json");
        var (ok, _, problems) = Check(engine, dir);
        Assert.False(ok);
        Assert.Contains("cannot be read", string.Join(" ", problems));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Rejects_a_runtimeconfig_that_declares_no_runtime_at_all(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        Put(dir, "MultiSeat.Service.runtimeconfig.json", "{ \"runtimeOptions\": { \"tfm\": \"net9.0\" } }");
        var (ok, _, problems) = Check(engine, dir);
        Assert.False(ok);
        Assert.Contains("neither includedFrameworks nor frameworks", string.Join(" ", problems));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Names_every_required_file_a_payload_lacks(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        File.Delete(Path.Combine(dir, "wwwroot", "index.html"));
        File.Delete(Path.Combine(dir, "appsettings.json"));
        var j = J(Ps(engine, "$r = @(Get-MissingPayloadFiles -Dir " + Q(dir) + ")"));
        Assert.Equal(new[] { "appsettings.json", "wwwroot\\index.html" }, Strings(j).OrderBy(x => x).ToArray());
    }

    // -- Shared runtime requirements -------------------------------------------------

    [Theory, MemberData(nameof(Engines))]
    public void Reads_the_required_frameworks_from_a_runtimeconfig(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        var j = J(Ps(engine, "$r = @(Get-RuntimeRequirements -RuntimeConfigPath " +
                             Q(Path.Combine(dir, "MultiSeat.Service.runtimeconfig.json")) + ")"));
        var rows = j.EnumerateArray().Select(x => x.GetProperty("Name").GetString() + " " +
                                                  x.GetProperty("Version").GetString() + " " +
                                                  x.GetProperty("RollForward").GetString()).ToArray();
        Assert.Equal(new[] { "Microsoft.NETCore.App 9.0.0 Minor", "Microsoft.AspNetCore.App 9.0.0 Minor" }, rows);
    }

    // required, installed, rollForward, expected
    public static IEnumerable<object[]> RollForwardCases()
    {
        yield return new object[] { "9.0.0", "9.0.20", "Minor", true };    // newer patch
        yield return new object[] { "9.0.5", "9.0.3", "Minor", false };    // older patch
        yield return new object[] { "9.0.0", "9.3.1", "Minor", true };     // newer minor, same major
        yield return new object[] { "9.0.0", "10.0.12", "Minor", false };  // major does not roll by default
        yield return new object[] { "9.0.0", "8.0.31", "Minor", false };   // older major
        yield return new object[] { "9.0.0", "10.0.12", "Major", true };
        yield return new object[] { "9.0.0", "10.0.12", "LatestMajor", true };
        yield return new object[] { "9.0.0", "9.1.0", "LatestPatch", false };
        yield return new object[] { "9.0.0", "9.0.4", "LatestPatch", true };
        yield return new object[] { "9.0.0", "9.0.4", "Disable", false };
        yield return new object[] { "9.0.4", "9.0.4", "Disable", true };
        yield return new object[] { "9.0.0", "9.0.20-rtm.1", "Minor", true }; // suffix ignored
    }

    [Theory, MemberData(nameof(Engines))]
    public void Follows_the_dotnet_roll_forward_policy(string engine)
    {
        var cases = RollForwardCases().Select(c => new { r = (string)c[0], i = (string)c[1], p = (string)c[2], e = (bool)c[3] }).ToArray();
        var json = JsonSerializer.Serialize(cases);
        var j = J(Ps(engine,
            "$rows = " + Q(json) + " | ConvertFrom-Json\n" +
            "$r = @($rows | ForEach-Object { [bool](Test-RuntimeVersionSatisfies -Required $_.r -Installed $_.i -RollForward $_.p) })"));
        var actual = j.EnumerateArray().Select(x => x.GetBoolean()).ToArray();
        for (var n = 0; n < cases.Length; n++)
            Assert.True(cases[n].e == actual[n],
                $"required {cases[n].r}, installed {cases[n].i}, {cases[n].p}: expected {cases[n].e}, got {actual[n]}");
    }

    private static (bool Ok, string[] Missing) Preflight(string engine, string runtimeConfigDir, params string[] hostLines)
    {
        var lines = "@(" + string.Join(",", hostLines.Select(Q)) + ")";
        var j = J(Ps(engine,
            "$req = @(Get-RuntimeRequirements -RuntimeConfigPath " + Q(Path.Combine(runtimeConfigDir, "MultiSeat.Service.runtimeconfig.json")) + ")\n" +
            "$r = Test-HostHasRuntimes -Requirements $req -RuntimeLines " + lines));
        return (j.GetProperty("Ok").GetBoolean(), Strings(j.GetProperty("Missing")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Preflight_passes_when_the_host_has_both_shared_runtimes(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        var (ok, missing) = Preflight(engine, dir,
            @"Microsoft.AspNetCore.App 9.0.20 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]",
            @"Microsoft.NETCore.App 9.0.20 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]");
        Assert.True(ok, string.Join(" | ", missing));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Preflight_names_the_runtime_a_host_lacks(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        var (ok, missing) = Preflight(engine, dir,
            @"Microsoft.NETCore.App 9.0.20 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]");
        Assert.False(ok);
        var one = Assert.Single(missing);
        Assert.Contains("Microsoft.AspNetCore.App", one);
        Assert.Contains("none installed", one);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Preflight_rejects_a_host_with_only_an_older_major(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        var (ok, missing) = Preflight(engine, dir,
            @"Microsoft.AspNetCore.App 8.0.31 [x]", @"Microsoft.NETCore.App 8.0.31 [x]",
            @"Microsoft.AspNetCore.App 10.0.12 [x]", @"Microsoft.NETCore.App 10.0.12 [x]");
        Assert.False(ok);
        Assert.Equal(2, missing.Length);
        Assert.Contains("installed: 8.0.31, 10.0.12", missing[0]);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Preflight_fails_a_host_with_no_dotnet_at_all(string engine)
    {
        using var t = new Temp();
        var dir = FrameworkDependent(t.Sub("p"));
        var (ok, missing) = Preflight(engine, dir);
        Assert.False(ok);
        Assert.Equal(2, missing.Length);
    }

    // -- Replacing the install folder ------------------------------------------------

    private static string InstallCall(string stage, string install, string root, string stamp = "20261009-120000", string extra = "") =>
        "$info = Install-Payload -Stage " + Q(stage) + " -InstallDir " + Q(install) +
        " -ConfigBackupRoot " + Q(Path.Combine(root, "config-backups")) +
        " -FolderBackupRoot " + Q(Path.Combine(root, "install-backups")) + " -Stamp " + Q(stamp) + extra + "\n";

    // appsettings bytes a host really has: a BOM, CRLF, no trailing newline. A text round trip would change them.
    private static readonly byte[] HostSettingsBytes = new byte[] { 0xEF, 0xBB, 0xBF }
        .Concat(Encoding.UTF8.GetBytes("{\r\n  \"MultiSeat\": { \"A\": 7 }\r\n}")).ToArray();
    private static readonly byte[] HostLocalBytes = new byte[] { 0xEF, 0xBB, 0xBF }
        .Concat(Encoding.UTF8.GetBytes("{ \"MultiSeat\": { \"ApiKey\": \"k\" } }")).ToArray();

    private static string PopulatedSelfContainedInstall(Temp t)
    {
        var install = SelfContained(t.Sub("install"), "old");
        Put(install, "appsettings.json", HostSettingsBytes);
        Put(install, "appsettings.local.json", HostLocalBytes);
        return install;
    }

    [Theory, MemberData(nameof(Engines))]
    public void Replaces_a_self_contained_folder_with_a_framework_dependent_build_cleanly(string engine)
    {
        using var t = new Temp();
        var install = PopulatedSelfContainedInstall(t);
        var before = Hashes(install);
        var stage = FrameworkDependent(t.Sub("stage"), "new");

        var j = J(Ps(engine, InstallCall(stage, install, t.Root) +
                             "$r = [pscustomobject]@{ Check = (Test-PayloadConsistency -Dir " + Q(install) + "); Info = $info }"));

        var check = j.GetProperty("Check");
        Assert.True(check.GetProperty("Ok").GetBoolean(), string.Join(" | ", Strings(check.GetProperty("Problems"))));
        Assert.Equal("framework-dependent", check.GetProperty("Mode").GetString());
        foreach (var gone in new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll" })
            Assert.False(File.Exists(Path.Combine(install, gone)), gone + " survived the replace");

        // New payload is in, the host's own files are untouched, byte for byte.
        Assert.Equal("exe-new", File.ReadAllText(Path.Combine(install, "MultiSeat.Service.exe")));
        Assert.Equal(HostSettingsBytes, File.ReadAllBytes(Path.Combine(install, "appsettings.json")));
        Assert.Equal(HostLocalBytes, File.ReadAllBytes(Path.Combine(install, "appsettings.local.json")));

        // Backups exist and hold the OLD folder, including its bundled runtime.
        var folderBackup = Path.Combine(t.Root, "install-backups", "20261009-120000");
        Assert.Equal(before, Hashes(folderBackup));
        Assert.Equal(HostLocalBytes, File.ReadAllBytes(Path.Combine(t.Root, "config-backups", "20261009-120000", "appsettings.local.json")));
        Assert.Equal(HostSettingsBytes, File.ReadAllBytes(Path.Combine(t.Root, "config-backups", "20261009-120000", "appsettings.json")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Repairs_the_exact_hybrid_folder_left_by_an_earlier_publish(string engine)
    {
        using var t = new Temp();
        var install = FrameworkDependent(t.Sub("install"), "old");
        Put(install, "hostfxr.dll", "leftover");                  // the 2026-10-08 hybrid
        Put(install, "appsettings.local.json", HostLocalBytes);
        Assert.False(Check(engine, install).Ok);

        var stage = FrameworkDependent(t.Sub("stage"), "new");
        var j = J(Ps(engine, InstallCall(stage, install, t.Root) + "$r = Test-PayloadConsistency -Dir " + Q(install)));
        Assert.True(j.GetProperty("Ok").GetBoolean(), string.Join(" | ", Strings(j.GetProperty("Problems"))));
        Assert.False(File.Exists(Path.Combine(install, "hostfxr.dll")));
        Assert.Equal(HostLocalBytes, File.ReadAllBytes(Path.Combine(install, "appsettings.local.json")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Replaces_a_framework_dependent_folder_with_a_self_contained_release(string engine)
    {
        using var t = new Temp();
        var install = FrameworkDependent(t.Sub("install"), "old");
        var stage = SelfContained(t.Sub("stage"), "new");
        // The release asset also carries installer files, which must not land in the install folder.
        Put(stage, "scripts/install-service.ps1", "x");
        Put(stage, "prerequisites/p.ps1", "x");
        Put(stage, "README.md", "x");

        var j = J(Ps(engine, InstallCall(stage, install, t.Root) + "$r = Test-PayloadConsistency -Dir " + Q(install)));
        Assert.True(j.GetProperty("Ok").GetBoolean(), string.Join(" | ", Strings(j.GetProperty("Problems"))));
        Assert.Equal("self-contained", j.GetProperty("Mode").GetString());
        Assert.False(Directory.Exists(Path.Combine(install, "scripts")));
        Assert.False(Directory.Exists(Path.Combine(install, "prerequisites")));
        Assert.False(File.Exists(Path.Combine(install, "README.md")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Installs_into_a_folder_that_does_not_exist_yet(string engine)
    {
        using var t = new Temp();
        var stage = FrameworkDependent(t.Sub("stage"), "new");
        var install = t.Sub("install");
        var j = J(Ps(engine, InstallCall(stage, install, t.Root) + "$r = Test-PayloadConsistency -Dir " + Q(install)));
        Assert.True(j.GetProperty("Ok").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(t.Root, "install-backups")), "nothing existed to back up");
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_failure_midway_puts_the_previous_folder_back(string engine)
    {
        using var t = new Temp();
        var install = PopulatedSelfContainedInstall(t);
        var before = Hashes(install);
        var stage = FrameworkDependent(t.Sub("stage"), "new");
        // Copied last (alphabetical), so the wipe and most of the copy have already happened.
        Put(stage, "zzz-late.dll", "locked");

        PsResult res;
        using (new FileStream(Path.Combine(stage, "zzz-late.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            res = Ps(engine, InstallCall(stage, install, t.Root, extra: "") + "$r = 'unreachable'");
        }

        Assert.NotEqual(0, res.ExitCode);
        Assert.Contains("Install failed", res.Stdout + res.Stderr);
        Assert.Contains("was restored from", res.Stdout + res.Stderr);
        // Byte-identical to the folder before the attempt: old runtime, old config, host files.
        Assert.Equal(before, Hashes(install));
        Assert.False(File.Exists(Path.Combine(install, "zzz-late.dll")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_failed_install_reports_that_it_restored_the_folder(string engine)
    {
        // The caller starts the service again only when Data['Restored'] is not $false.
        using var t = new Temp();
        var install = PopulatedSelfContainedInstall(t);
        var stage = FrameworkDependent(t.Sub("stage"), "new");
        Put(stage, "zzz-late.dll", "locked");
        PsResult res;
        using (new FileStream(Path.Combine(stage, "zzz-late.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            res = Ps(engine,
                "try { " + InstallCall(stage, install, t.Root) + " $r = 'no error' } " +
                "catch { $r = [pscustomobject]@{ Restored = $_.Exception.Data['Restored']; Message = $_.Exception.Message } }");
        }
        var j = J(res);
        Assert.True(j.GetProperty("Restored").GetBoolean());
        Assert.Contains("Install failed", j.GetProperty("Message").GetString());
    }

    [Theory, MemberData(nameof(Engines))]
    public void Names_settings_a_new_build_adds_even_though_the_shipped_file_has_comments(string engine)
    {
        // The real shipped appsettings.json has // comment lines; Windows PowerShell 5.1 cannot
        // parse those with ConvertFrom-Json, which used to hide this report entirely.
        using var t = new Temp();
        var install = FrameworkDependent(t.Sub("install"), "old");
        Put(install, "appsettings.json", "{ \"MultiSeat\": { \"MaxSeats\": 4 } }");
        var stage = FrameworkDependent(t.Sub("stage"), "new");
        File.Copy(Path.Combine(RepoRoot, "src", "MultiSeat.Service", "appsettings.json"), Path.Combine(stage, "appsettings.json"), true);

        var res = Ps(engine, InstallCall(stage, install, t.Root) + "$r = 1");
        var text = res.Stdout + res.Stderr;
        Assert.Contains("setting(s) your appsettings.json does not have", text);
        Assert.DoesNotContain("could not compare settings", text);
        Assert.Equal("{ \"MultiSeat\": { \"MaxSeats\": 4 } }", File.ReadAllText(Path.Combine(install, "appsettings.json")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Backups_are_not_readable_by_ordinary_users(string engine)
    {
        // appsettings.local.json can hold the API key; ProgramData lets every user read by default.
        using var t = new Temp();
        var install = PopulatedSelfContainedInstall(t);
        var stage = FrameworkDependent(t.Sub("stage"), "new");
        Ps(engine, InstallCall(stage, install, t.Root) + "$r = 1");
        foreach (var d in new[] { "config-backups", "install-backups" })
        {
            // Read the ACL in-process: Get-Acl needs a module that is not always loadable in a child shell.
            var sec = new DirectoryInfo(Path.Combine(t.Root, d)).GetAccessControl();
            Assert.True(sec.AreAccessRulesProtected, d + " still inherits its ACL");
            var ids = sec.GetAccessRules(true, true, typeof(System.Security.Principal.NTAccount))
                         .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                         .Select(r => r.IdentityReference.Value).ToArray();
            Assert.DoesNotContain("BUILTIN\\Users", ids);
            Assert.DoesNotContain("Everyone", ids);
            Assert.Contains("NT AUTHORITY\\SYSTEM", ids);
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_file_the_service_still_holds_aborts_the_install_and_restores_the_folder(string engine)
    {
        // The realistic failure: the wipe cannot delete a locked DLL after others are already gone.
        using var t = new Temp();
        var install = PopulatedSelfContainedInstall(t);
        var before = Hashes(install);
        var stage = FrameworkDependent(t.Sub("stage"), "new");

        PsResult res;
        // Readers allowed (the backup copy must work), deleters and writers refused.
        using (new FileStream(Path.Combine(install, "MultiSeat.Service.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            res = Ps(engine,
                "try { " + InstallCall(stage, install, t.Root) + " $r = 'no error' } " +
                "catch { $r = [pscustomobject]@{ Restored = $_.Exception.Data['Restored']; Message = $_.Exception.Message } }");
        }
        var j = J(res);
        Assert.True(j.GetProperty("Restored").GetBoolean(), j.GetProperty("Message").GetString());
        Assert.Equal(before, Hashes(install));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_post_copy_inconsistency_is_caught_and_rolled_back(string engine)
    {
        // Skipping the runtimeconfig leaves an installed folder that contradicts itself even
        // though the stage was fine: the check on the INSTALLED folder must catch it.
        using var t = new Temp();
        var install = PopulatedSelfContainedInstall(t);
        var before = Hashes(install);
        var stage = FrameworkDependent(t.Sub("stage"), "new");

        var res = Ps(engine, InstallCall(stage, install, t.Root, extra: " -SkipNames @('MultiSeat.Service.runtimeconfig.json')") + "$r = 'unreachable'");
        Assert.NotEqual(0, res.ExitCode);
        Assert.Contains("inconsistent after copying", res.Stdout + res.Stderr);
        Assert.Equal(before, Hashes(install));
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_inconsistent_stage_is_refused_before_anything_is_touched(string engine)
    {
        using var t = new Temp();
        var install = PopulatedSelfContainedInstall(t);
        var before = Hashes(install);
        var stage = FrameworkDependent(t.Sub("stage"), "new");
        Put(stage, "hostfxr.dll", "leftover");                    // hybrid stage

        var res = Ps(engine, InstallCall(stage, install, t.Root) + "$r = 'unreachable'");
        Assert.NotEqual(0, res.ExitCode);
        Assert.Contains("staged payload is inconsistent", res.Stdout + res.Stderr);
        Assert.Equal(before, Hashes(install));
        Assert.False(Directory.Exists(Path.Combine(t.Root, "install-backups")), "no backup should be made for a refused stage");
        Assert.False(Directory.Exists(Path.Combine(t.Root, "config-backups")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_stage_missing_a_required_file_is_refused_before_anything_is_touched(string engine)
    {
        using var t = new Temp();
        var install = PopulatedSelfContainedInstall(t);
        var before = Hashes(install);
        var stage = FrameworkDependent(t.Sub("stage"), "new");
        File.Delete(Path.Combine(stage, "wwwroot", "index.html"));

        var res = Ps(engine, InstallCall(stage, install, t.Root) + "$r = 'unreachable'");
        Assert.NotEqual(0, res.ExitCode);
        Assert.Contains("wwwroot\\index.html", res.Stdout + res.Stderr);
        Assert.Equal(before, Hashes(install));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Keeps_only_the_newest_full_folder_backups(string engine)
    {
        using var t = new Temp();
        var install = FrameworkDependent(t.Sub("install"), "old");
        var backups = Path.Combine(t.Root, "install-backups");
        foreach (var s in new[] { "20260101-000000", "20260102-000000", "20260103-000000", "20260104-000000" })
            Put(Path.Combine(backups, s), "marker.txt", s);
        var stage = FrameworkDependent(t.Sub("stage"), "new");

        Ps(engine, InstallCall(stage, install, t.Root, "20261009-120000") + "$r = 1");

        var left = Directory.GetDirectories(backups).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "20260103-000000", "20260104-000000", "20261009-120000" }, left);
    }

    // -- Wiring of install-service.ps1 itself ----------------------------------------

    private static string[] InstallerLines() => File.ReadAllLines(Path.Combine(RepoRoot, "scripts", "install-service.ps1"));

    [Fact]
    public void The_source_build_never_publishes_into_the_install_folder()
    {
        var publishes = InstallerLines().Where(l => l.TrimStart().StartsWith("dotnet publish")).ToArray();
        Assert.NotEmpty(publishes);
        foreach (var l in publishes)
        {
            Assert.DoesNotContain("InstallDir", l);
            Assert.Contains("$stage", l);
        }
    }

    [Fact]
    public void The_installer_loads_the_library_and_both_paths_use_the_shared_replace()
    {
        var text = string.Join("\n", InstallerLines());
        // An actual dot-source statement, not a mention in a comment.
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"^\.\s+\(Join-Path \$PSScriptRoot ""lib\\install-lib\.ps1""\)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline), text);
        Assert.True(File.Exists(Lib));
        // One replace routine, called once, after the verification and the runtime preflight.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"Install-Payload -Stage"));
        var iVerify = text.IndexOf("Test-PayloadConsistency -Dir $stage", StringComparison.Ordinal);
        var iPre = text.IndexOf("Test-HostHasRuntimes", StringComparison.Ordinal);
        // First stop AFTER the staging section begins (the -Uninstall block stops the service too).
        var iStop = text.IndexOf("Stop-Service $ServiceName", text.IndexOf("$stamp = Get-Date", StringComparison.Ordinal), StringComparison.Ordinal);
        var iInstall = text.IndexOf("Install-Payload -Stage", StringComparison.Ordinal);
        Assert.True(iVerify > 0 && iVerify < iPre && iPre < iStop && iStop < iInstall,
            "order must be: verify stage, runtime preflight, stop service, replace folder");
        // The old in-place wipe must not come back as a second, unshared copy.
        Assert.DoesNotContain("Get-ChildItem $InstallDir -Force | Remove-Item", text);
    }
}
