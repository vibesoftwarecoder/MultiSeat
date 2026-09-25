using Microsoft.Extensions.Logging;
using MultiSeat.Service.Streaming;
using Xunit;

namespace MultiSeat.Tests.Streaming;

/// <summary>
/// Issue #63, second half. Pointing file_apps at the seat's own copy (0.6.8) did not stop a clean
/// install from failing, because Apollo creates {exe_dir}/config/ and copies assets/apps.json to
/// the DEFAULT path before it reads file_apps — and a seat cannot write under Program Files.
/// <see cref="ApolloInstallSeed"/> does both as SYSTEM before each launch.
///
/// Every case builds a stand-in install under %TEMP%, so the result does not depend on whether
/// ApolloVibe is installed on the machine running the tests — the mistake that let #63 ship.
/// </summary>
public class ApolloInstallSeedTests
{
    private const string ShippedApps = """{"env":{},"apps":[{"name":"Desktop"}]}""";

    private static string NewFakeInstall(bool withAssets)
    {
        var root = Path.Combine(Path.GetTempPath(), $"multiseat-apollo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        if (withAssets)
        {
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            File.WriteAllText(Path.Combine(root, "assets", "apps.json"), ShippedApps);
        }
        return root;
    }

    private static void DeleteFakeInstall(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Seeds_TheInstallAppsJson_WhenConfigFolderIsMissing()
    {
        // The state a fresh ApolloVibe zip extract leaves behind: assets\ and no config\.
        var root = NewFakeInstall(withAssets: true);
        try
        {
            var logger = new RecordingLogger();

            var outcome = ApolloInstallSeed.EnsureInstallAppsJson(root, logger);

            Assert.Equal(ApolloInstallSeed.Outcome.Seeded, outcome);
            Assert.True(Directory.Exists(Path.Combine(root, "config")),
                "config folder was not created - Apollo's make_directory(appdata()) would fail as a seat");
            Assert.Equal(ShippedApps, File.ReadAllText(Path.Combine(root, "config", "apps.json")));
            // A reporter's Event Log only carries Information and above, so what was done must be
            // visible there.
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Seeded"));
            // The temp file the copy goes through must be gone once the move has happened.
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "config"), "*.tmp"));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "config")));
        }
        finally { DeleteFakeInstall(root); }
    }

    [Fact]
    public void DoesNotOverwrite_AnExistingInstallAppsJson()
    {
        // Once Apollo or the user has written this file it is the console install's app list.
        // Replacing it with the shipped default would silently delete their games.
        var root = NewFakeInstall(withAssets: true);
        try
        {
            const string curated = """{"apps":[{"name":"Curated Game"}]}""";
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var dest = Path.Combine(root, "config", "apps.json");
            File.WriteAllText(dest, curated);
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(dest, stamp);

            var first = ApolloInstallSeed.EnsureInstallAppsJson(root, new RecordingLogger());
            var second = ApolloInstallSeed.EnsureInstallAppsJson(root, new RecordingLogger());

            Assert.Equal(ApolloInstallSeed.Outcome.AlreadyPresent, first);
            Assert.Equal(ApolloInstallSeed.Outcome.AlreadyPresent, second);
            Assert.Equal(curated, File.ReadAllText(dest));
            // A replace-with-identical-content would keep the text but bump the timestamp.
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(dest));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "config"), "*.tmp"));
        }
        finally { DeleteFakeInstall(root); }
    }

    [Fact]
    public void CreatesTheConfigFolder_EvenWithNothingToCopy()
    {
        // The two Apollo steps fail independently, so the folder is still worth creating when
        // assets\apps.json is absent - and the missing source must be reported, because Apollo
        // is then certain to exit at startup.
        var root = NewFakeInstall(withAssets: false);
        try
        {
            var logger = new RecordingLogger();

            var outcome = ApolloInstallSeed.EnsureInstallAppsJson(root, logger);

            Assert.Equal(ApolloInstallSeed.Outcome.NoSource, outcome);
            Assert.True(Directory.Exists(Path.Combine(root, "config")));
            Assert.False(File.Exists(Path.Combine(root, "config", "apps.json")));
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        }
        finally { DeleteFakeInstall(root); }
    }

    [Fact]
    public void SurvivesAnIoFailure_AndWarns()
    {
        // A FILE named "config" makes Directory.CreateDirectory throw a real IOException - no
        // seam needed. The seat launch must go on (log and return), never crash on this.
        var root = NewFakeInstall(withAssets: true);
        try
        {
            var blocker = Path.Combine(root, "config");
            File.WriteAllText(blocker, "not a folder");
            var logger = new RecordingLogger();

            var outcome = ApolloInstallSeed.EnsureInstallAppsJson(root, logger);

            Assert.Equal(ApolloInstallSeed.Outcome.Failed, outcome);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is not null);
            Assert.Equal("not a folder", File.ReadAllText(blocker));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { DeleteFakeInstall(root); }
    }

    [Fact]
    public void CreatesNothing_WhenTheInstallDirectoryIsMissing()
    {
        // No install means nothing to seed. Creating the root would make a phantom ApolloVibe
        // folder under Program Files that the readiness check then mistakes for an install.
        var root = Path.Combine(Path.GetTempPath(), $"multiseat-apollo-{Guid.NewGuid():N}");
        try
        {
            var logger = new RecordingLogger();

            var outcome = ApolloInstallSeed.EnsureInstallAppsJson(root, logger);

            Assert.Equal(ApolloInstallSeed.Outcome.RootMissing, outcome);
            Assert.False(Directory.Exists(root), "the install root was created");
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        }
        finally { DeleteFakeInstall(root); }
    }

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
}
