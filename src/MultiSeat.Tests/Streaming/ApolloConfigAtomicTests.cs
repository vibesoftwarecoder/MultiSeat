using System.Text;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// G9: Apollo configuration/state replacement must never expose a partial file to Apollo.
/// <see cref="ApolloConfigBuilder"/> routes its live-file overwrites through
/// <see cref="AtomicFile"/> (complete same-directory temp + rename), so readers observe
/// the old complete file or the new complete file.
///
/// A true crash-mid-write cannot be tested deterministically, so RED for the old code is
/// a source-level proof: BuildConfig, UpdateDisplayOutput, the apps.json seed, the state
/// seed and WriteStateFile all used <c>File.WriteAllText(target, ...)</c>, which truncates
/// the target before streaming content. These tests pin the post-fix invariants: byte
/// parity with the previous writer, old-file preservation on failure, no temp debris.
/// </summary>
public class ApolloConfigAtomicTests
{
    [Fact]
    public void UpdateDisplayOutput_ChangesOnlyTargetLine_ByteIdenticalElsewhere()
    {
        // Guards the File.WriteAllLines → join + AtomicFile refactor: every byte except the
        // single output_name line must be identical (including the trailing newline that
        // WriteAllLines emits after the last line).
        var tempDir = NewTempDir();
        try
        {
            var builder = NewBuilder();
            var seat = new SeatInfo { AccountName = "MultiSeatSeat01", PortBase = 47984 };
            var configPath = builder.BuildConfig(seat, tempDir);
            var before = File.ReadAllLines(configPath, Encoding.UTF8);

            builder.UpdateDisplayOutput(configPath, @"\\.\DISPLAY#SudoVDA#1");

            var after = File.ReadAllLines(configPath, Encoding.UTF8);
            Assert.Equal(before.Length, after.Length);
            var changed = before.Zip(after).Count(p => p.First != p.Second);
            Assert.Equal(1, changed);
            Assert.Contains(@"output_name = \\.\DISPLAY#SudoVDA#1", File.ReadAllText(configPath, Encoding.UTF8));

            var raw = File.ReadAllBytes(configPath);
            Assert.Equal((byte)'\n', raw[^1]);
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(configPath)!, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTestDir(tempDir);
        }
    }

    [Fact]
    public void UnpairClient_FailedWrite_PreservesPairingState()
    {
        // sunshine_state.json holds pairings; a failed rewrite must leave the previous
        // valid document intact rather than a torn file Apollo would reject.
        var tempDir = NewTempDir();
        try
        {
            var builder = NewBuilder();
            var stateDir = Path.Combine(tempDir, "MultiSeatSeat01", "config");
            Directory.CreateDirectory(stateDir);
            var statePath = Path.Combine(stateDir, "sunshine_state.json");
            const string oldContent = """{"root": {"named_devices": [{"name": "deck"}], "uniqueid": "OLD"}}""";
            File.WriteAllText(statePath, oldContent, Encoding.UTF8);
            File.SetAttributes(statePath, FileAttributes.ReadOnly);
            try
            {
                Assert.False(builder.UnpairClient("MultiSeatSeat01", tempDir, "deck"));
                Assert.Equal(oldContent, File.ReadAllText(statePath, Encoding.UTF8));
                Assert.Empty(Directory.EnumerateFiles(stateDir, "*.tmp"));
            }
            finally
            {
                File.SetAttributes(statePath, FileAttributes.Normal);
            }
        }
        finally
        {
            DeleteTestDir(tempDir);
        }
    }

    private static ApolloConfigBuilder NewBuilder() =>
        new(new TestLogger<ApolloConfigBuilder>(), Options.Create(new MultiSeatOptions()));

    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), $"multiseat-test-{Guid.NewGuid():N}");

    private static void DeleteTestDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
