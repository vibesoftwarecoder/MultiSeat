namespace MultiSeat.Service.Streaming;

/// <summary>
/// Seeds the one file Apollo insists on finding inside its own install directory (#63).
///
/// Apollo's startup (config.cpp) touches {exe_dir}/config/ twice BEFORE it reads any setting
/// from the seat's sunshine.conf:
///   1. make_directory(appdata()) creates {exe_dir}/config/ when it is missing;
///   2. apply_config() copies assets/apps.json to {exe_dir}/config/apps.json when that is missing.
/// Both run against the built-in default path, so the per-seat file_apps override added in 0.6.8
/// cannot help: it is applied after both. A seat runs Apollo as a standard user, which cannot
/// write under Program Files, so on an install missing either one Apollo logs "Failed to apply
/// config" before its log file is open, waits 10 seconds and exits. The ApolloVibe release zip
/// ships no config/ folder, so every clean install starts in that state until someone runs Apollo
/// elevated once.
///
/// The service runs as SYSTEM and can do both steps itself, which is what this does before each
/// seat launch. It never replaces an existing apps.json: once Apollo or the user has written one,
/// it is the console install's app list, not ours to reset.
/// </summary>
internal static class ApolloInstallSeed
{
    internal enum Outcome
    {
        /// <summary>config\apps.json already existed. Nothing was written to it.</summary>
        AlreadyPresent,
        /// <summary>assets\apps.json was copied to config\apps.json.</summary>
        Seeded,
        /// <summary>There is no assets\apps.json to copy from. Apollo will fail to start.</summary>
        NoSource,
        /// <summary>The install directory itself does not exist. Nothing was created.</summary>
        RootMissing,
        /// <summary>Creating the folder or copying the file failed. Logged; the launch goes ahead.</summary>
        Failed,
    }

    /// <summary>
    /// Ensure {apolloRoot}\config\ exists and holds an apps.json, copying assets\apps.json when it
    /// does not. The install directory itself is never created: without it there is no install to
    /// seed. The copy goes to a temp file beside the target and is moved into place without
    /// overwrite, so a concurrent launch never sees a half-written file. Never throws: a failure here is logged as a warning and the launch carries on,
    /// because Apollo may still start (for example if the file appears by other means), and the
    /// readiness check reports it if it does not.
    /// </summary>
    internal static Outcome EnsureInstallAppsJson(string apolloRoot, ILogger logger)
    {
        var configDir = Path.Combine(apolloRoot, "config");
        var dest = Path.Combine(configDir, "apps.json");
        var source = Path.Combine(apolloRoot, "assets", "apps.json");
        var tmp = Path.Combine(configDir, "apps.json." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            if (!Directory.Exists(apolloRoot))
            {
                logger.LogWarning(
                    "Apollo install directory {Root} does not exist, so there is nothing to seed. " +
                    "Apollo will not start until ApolloVibe is installed", apolloRoot);
                return Outcome.RootMissing;
            }

            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
                logger.LogInformation(
                    "Created Apollo's config folder {Dir} — a seat's Apollo cannot create it " +
                    "under the install directory itself (#63)", configDir);
            }

            if (File.Exists(dest))
            {
                logger.LogDebug("Apollo install apps.json already present at {Dest}", dest);
                return Outcome.AlreadyPresent;
            }

            if (!File.Exists(source))
            {
                logger.LogWarning(
                    "Neither {Dest} nor {Source} exists, so Apollo will exit at startup with " +
                    "\"Failed to apply config\". The ApolloVibe install is incomplete — re-run " +
                    "prerequisites\\install-prerequisites.ps1", dest, source);
                return Outcome.NoSource;
            }

            // File.Copy is not atomic, so copy beside the target and move the finished file in.
            // overwrite: false on the move, so a file that appeared since the check above is kept.
            File.Copy(source, tmp, overwrite: false);
            File.Move(tmp, dest, overwrite: false);
            logger.LogInformation(
                "Seeded Apollo's {Dest} from {Source} — a seat's Apollo cannot create it " +
                "under the install directory itself (#63)", dest, source);
            return Outcome.Seeded;
        }
        catch (IOException) when (File.Exists(dest))
        {
            // Lost a race with another launch, or with Apollo itself. The file is there, which is
            // all that was needed.
            return Outcome.AlreadyPresent;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not seed Apollo's {Dest} — on an install where it is missing, a seat's " +
                "Apollo will exit at startup with \"Failed to apply config\"", dest);
            return Outcome.Failed;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
