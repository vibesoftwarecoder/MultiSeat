using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// The stray-Apollo kill against real processes and real WMI (issue #124). The unit tests in
/// <see cref="ApolloSeatConfigMatchTests"/> cover the matching rule; this proves the whole path
/// finds a live process by its command line, kills it, and leaves a same-named process on another
/// seat's config alone.
/// </summary>
public class ApolloStrayKillTests
{
    private static readonly BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    // A copy of cmd.exe named sunshine.exe. "cmd /k rem <config>" stays alive while its redirected
    // stdin stays open, and the config path is a real argument on the command line.
    private static Process StartFakeApollo(string exe, string configPath)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("/k");
        start.ArgumentList.Add("rem");
        start.ArgumentList.Add(configPath);
        return Process.Start(start)!;
    }

    // WMI can lag a moment behind a process that has just started.
    private static List<int> WaitForWmi(MethodInfo query, string configPath, int expectedPid)
    {
        var seen = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            seen = (List<int>)query.Invoke(null, ["sunshine", configPath, null])!;
            if (seen.Contains(expectedPid)) break;
            Thread.Sleep(100);
        }
        return seen;
    }

    [Fact]
    public void KillStray_KillsOnlyTheApolloOnThatSeatsConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ms-straykill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "sunshine.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        var targetConfig = Path.Combine(directory, "seatA", "sunshine.conf");
        var otherConfig = Path.Combine(directory, "seatB", "sunshine.conf");

        // Any real Apollo on this machine must come through untouched.
        var realBefore = Process.GetProcessesByName("sunshine").Select(p => p.Id).ToHashSet();
        using var target = StartFakeApollo(exe, targetConfig);
        using var other = StartFakeApollo(exe, otherConfig);
        try
        {
            var manager = new ApolloManager(NullLogger<ApolloManager>.Instance,
                Options.Create(new MultiSeatOptions { ApolloExePath = exe }), null!, null!);
            var query = typeof(ApolloManager).GetMethod("QueryApolloPidsForConfig", NonPublic);
            var kill = typeof(ApolloManager).GetMethod("KillStrayApollosForConfig", NonPublic);
            Assert.NotNull(query); // a renamed method must fail loudly, not pass vacuously
            Assert.NotNull(kill);

            // Positive control: WMI sees each fake under its own config only.
            Assert.Equal([target.Id], WaitForWmi(query, targetConfig, target.Id));
            Assert.Equal([other.Id], WaitForWmi(query, otherConfig, other.Id));

            // Negative control: a config no process runs kills nothing.
            Assert.Equal(0, (int)kill.Invoke(manager, [Path.Combine(directory, "nobody", "sunshine.conf"), "control"])!);
            Assert.False(target.HasExited);
            Assert.False(other.HasExited);

            Assert.Equal(1, (int)kill.Invoke(manager, [targetConfig, "test"])!);
            Assert.True(target.WaitForExit(5000));
            Assert.False(other.HasExited);

            var realAfter = Process.GetProcessesByName("sunshine").Select(p => p.Id).ToHashSet();
            Assert.True(realBefore.IsSubsetOf(realAfter), "a sunshine.exe that was not ours was killed");

            // Nothing is left on that config.
            Assert.Equal(0, (int)kill.Invoke(manager, [targetConfig, "again"])!);
        }
        finally
        {
            try { if (!target.HasExited) target.Kill(true); } catch { }
            try { if (!other.HasExited) other.Kill(true); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
