using MultiSeat.Service.Updates;
using Xunit;

namespace MultiSeat.Tests.Updates;

public class ReleaseSelectorTests
{
    private const UpdateComponent MS = UpdateComponent.MultiSeat;
    private const UpdateComponent AV = UpdateComponent.ApolloVibe;
    private const UpdateComponent MV = UpdateComponent.MoonlightVibe;

    private static GitHubRelease Rel(string tag, string published = "2026-01-01T00:00:00Z", bool draft = false,
        bool pre = false, string? body = null, string? target = null) =>
        new(tag, DateTimeOffset.Parse(published), target, body, [], draft, pre);

    private static ReleaseCandidate Cand(string tag, string published = "2026-01-01T00:00:00Z") =>
        new() { Tag = tag, PublishedAt = DateTimeOffset.Parse(published) };

    // ── BuildCandidates ───────────────────────────────────────────────

    [Fact]
    public void BuildCandidates_SkipsDraftsAndPreReleases_EvenWithAParseableTag()
    {
        var list = new[]
        {
            Rel("v0.6.19"),
            Rel("v0.7.0", draft: true),
            Rel("v0.8.0", pre: true),
        };

        var result = ReleaseSelector.BuildCandidates(MS, list);

        Assert.Equal(["v0.6.19"], result.Select(c => c.Tag));
    }

    [Fact]
    public void BuildCandidates_SkipsTagsOutsideTheGrammar()
    {
        var list = new[]
        {
            Rel("v2026.6.1-ms6"),
            Rel("test-no-priority-hack-2026-09-06"),
            Rel("debug-probe-instrumented-2026-09-06"),
            Rel("v2026.6.1"),
        };

        Assert.Equal(["v2026.6.1-ms6"], ReleaseSelector.BuildCandidates(AV, list).Select(c => c.Tag));
    }

    [Fact]
    public void BuildCandidates_ApolloTakesTheSunshineHashFromTheNotes_OtherComponentsDoNot()
    {
        var hash = new string('a', 64);
        var notes = $"SHA-256 of `sunshine.exe`:\n`{hash}`\n";

        var apollo = ReleaseSelector.BuildCandidates(AV, [Rel("v2026.6.1-ms6", body: notes, target: "abc")]);
        var multi = ReleaseSelector.BuildCandidates(MS, [Rel("v0.6.19", body: notes)]);

        Assert.Equal(hash, apollo.Single().SunshineSha256);
        Assert.Equal("abc", apollo.Single().TargetCommit);
        Assert.Null(multi.Single().SunshineSha256);
    }

    [Fact]
    public void BuildCandidates_KeepsNoReleaseNotesAndNoUrl()
    {
        var c = ReleaseSelector.BuildCandidates(MS, [Rel("v0.6.19", body: "secret notes")]).Single();

        // The candidate type has no field that could carry them: the notes never reach the state file.
        Assert.DoesNotContain(typeof(ReleaseCandidate).GetProperties(), p => p.Name is "Body" or "HtmlUrl" or "Url");
        Assert.Equal("v0.6.19", c.Tag);
    }

    // ── SelectLatest ──────────────────────────────────────────────────

    [Fact]
    public void SelectLatest_TakesTheMaximum_NotTheFirstAndNotTheNewestCreated()
    {
        // GitHub lists newest-created first. Here a hotfix for an older line was created last.
        var list = new[]
        {
            Cand("v0.6.5", "2026-09-30T00:00:00Z"),
            Cand("v0.6.19", "2026-09-01T00:00:00Z"),
            Cand("v0.6.9", "2026-08-01T00:00:00Z"),
        };

        Assert.Equal("v0.6.19", ReleaseSelector.SelectLatest(MS, list)!.Tag);
    }

    [Fact]
    public void SelectLatest_ApolloMs10BeatsMs9()
    {
        var list = new[] { Cand("v2026.6.1-ms9"), Cand("v2026.6.1-ms10"), Cand("v2026.6.1-ms2") };

        Assert.Equal("v2026.6.1-ms10", ReleaseSelector.SelectLatest(AV, list)!.Tag);
    }

    [Fact]
    public void SelectLatest_IgnoresUnparseableTags()
    {
        var list = new[] { Cand("moonlightvibe-win-6.3.1-efabf3"), Cand("v6.3.9"), Cand("v6.1.0-multiseat.1"), Cand("garbage") };

        Assert.Equal("v6.3.9", ReleaseSelector.SelectLatest(MV, list)!.Tag);
    }

    [Fact]
    public void SelectLatest_NothingParses_ReturnsNull()
    {
        Assert.Null(ReleaseSelector.SelectLatest(MS, []));
        Assert.Null(ReleaseSelector.SelectLatest(MS, [Cand("nightly"), Cand("test-1")]));
    }

    [Fact]
    public void SelectLatest_SameVersionUnderTwoTags_PrefersTheLaterPublishDate()
    {
        var older = Cand("v6.3.9", "2026-01-01T00:00:00Z");
        var newer = Cand("6.3.9", "2026-02-01T00:00:00Z");

        Assert.Same(newer, ReleaseSelector.SelectLatest(MV, [older, newer]));
        Assert.Same(newer, ReleaseSelector.SelectLatest(MV, [newer, older]));
    }

    [Fact]
    public void SelectLatest_SameVersionAndDate_KeepsTheFirst()
    {
        var a = Cand("v6.3.9");
        var b = Cand("6.3.9");

        Assert.Same(a, ReleaseSelector.SelectLatest(MV, [a, b]));
    }

    // ── Classify ──────────────────────────────────────────────────────

    private static ReleaseVersion V(UpdateComponent c, string s) => ReleaseVersion.TryParse(c, s)!;

    [Fact]
    public void Classify_ComparesInstalledWithLatest()
    {
        Assert.Equal(UpdateStatus.UpdateAvailable, ReleaseSelector.Classify(V(MS, "0.6.9"), V(MS, "0.6.19")));
        Assert.Equal(UpdateStatus.UpToDate, ReleaseSelector.Classify(V(MS, "0.6.19+b7e6d9539"), V(MS, "v0.6.19")));
        // 8: a local build ahead of the newest release is never "update available"
        Assert.Equal(UpdateStatus.Ahead, ReleaseSelector.Classify(V(MS, "0.6.20+abc"), V(MS, "0.6.19")));
        Assert.Equal(UpdateStatus.Ahead, ReleaseSelector.Classify(V(MS, "0.7.0"), V(MS, "0.7.0-rc1")));
        Assert.Equal(UpdateStatus.UpdateAvailable, ReleaseSelector.Classify(V(MS, "0.7.0-rc1"), V(MS, "0.7.0")));
    }

    [Fact]
    public void Classify_NoLatest_IsUnavailable_WhateverIsInstalled()
    {
        // 12
        Assert.Equal(UpdateStatus.Unavailable, ReleaseSelector.Classify(V(MS, "0.6.19"), null));
        Assert.Equal(UpdateStatus.Unavailable, ReleaseSelector.Classify(null, null));
    }

    [Fact]
    public void Classify_UnknownInstalled_NeverSaysUpdateAvailable()
    {
        Assert.Equal(UpdateStatus.UnknownInstalled, ReleaseSelector.Classify(null, V(AV, "v2026.6.1-ms6")));
    }
}
