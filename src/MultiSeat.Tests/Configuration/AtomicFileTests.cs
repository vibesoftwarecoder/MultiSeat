using System.Text;
using MultiSeat.Service.Configuration;
using Xunit;

namespace MultiSeat.Tests.Configuration;

/// <summary>
/// G9: configuration/state replacement must never expose a partial file. <see cref="AtomicFile"/>
/// writes the complete content to a same-directory temp file and renames it over the target,
/// so readers observe the old complete file or the new complete file.
///
/// A true crash-mid-write cannot be tested deterministically (no kill-timing or power-loss
/// simulation), so RED for the old code is a source-level proof: every replaced call site
/// used <c>File.WriteAllText(target, ...)</c>, which truncates the target before streaming
/// content — a crash in that window leaves a torn live file. These tests pin the post-fix
/// invariants deterministically: success content/encoding, failure preservation, no debris.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"multiseat-atomic-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Success_WritesExactContentWithRequestedEncoding()
    {
        var path = Path.Combine(_dir, "sunshine.conf");
        const string content = "port = 48100\noutput_name = {uuid}\n# trailing comment\n";

        AtomicFile.WriteAllText(path, content, Encoding.UTF8);

        Assert.Equal(content, File.ReadAllText(path, Encoding.UTF8));
        // Byte-identical to what the replaced direct File.WriteAllText(target, ..., same
        // encoding) calls produced: atomicity must not change encoding, newlines, or BOM
        // policy (Encoding.UTF8 emits a preamble here, as before — Apollo already parses it).
        var reference = Path.Combine(_dir, "reference.conf");
        File.WriteAllText(reference, content, Encoding.UTF8);
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(path));
    }

    [Fact]
    public void Success_ReplacesExistingFile_AndLeavesNoTempDebris()
    {
        var path = Path.Combine(_dir, "apps.json");
        File.WriteAllText(path, """{"apps": []}""", Encoding.UTF8);

        AtomicFile.WriteAllText(path, """{"apps": [{"name": "x"}]}""", Encoding.UTF8);

        Assert.Equal("""{"apps": [{"name": "x"}]}""", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void FailedReplacement_PreservesOldFile_AndCleansUpTemp()
    {
        // A read-only target makes the rename fail deterministically: the previous valid
        // file must remain intact, the staging temp must be removed, and the error must
        // propagate so callers keep their existing failure semantics.
        var path = Path.Combine(_dir, "sunshine_state.json");
        const string oldContent = """{"root": {"uniqueid": "OLD"}}""";
        File.WriteAllText(path, oldContent, Encoding.UTF8);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => AtomicFile.WriteAllText(path, """{"root": {"uniqueid": "NEW"}}""", Encoding.UTF8));

            Assert.Equal(oldContent, File.ReadAllText(path, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
