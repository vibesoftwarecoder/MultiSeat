using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MultiSeat.Service.Streaming;

/// <summary>
/// Sends the Steam entries of a seat's Apollo app list through the Steam isolation launcher.
///
/// Apollo starts its own app entries directly, so the service's launch path never sees them. A
/// <c>steam://</c> link goes through Windows' protocol handler, which runs <c>steam.exe</c> with no
/// hook and can hand the command to another session's Steam. This rewrite is what makes the
/// "Steam" tile in Moonlight isolated.
///
/// Only <c>cmd</c> is changed, because the launcher has to stay the app's own process: a
/// <c>detached</c> command would leave the supervisor with nothing to watch. A <c>steam://</c> item
/// in <c>detached</c> moves to <c>cmd</c>, and a <c>steam://</c> prep command is blanked, since it
/// would run through the same unwrapped handler.
/// </summary>
internal static class SteamAppListRewriter
{
    // An optional directory part (spaces allowed, so an unquoted "C:\Program Files (x86)\..."
    // works), then steam or steam.exe, then the end or whitespace. Anything else is not Steam.
    private static readonly Regex SteamCommand = new(
        @"^\s*""?(?<exe>(?:[^""]*[\\/])?steam(?:\.exe)?)""?(?:\s+(?<rest>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns the app list with Steam entries wrapped. The text is returned unchanged when it
    /// cannot be parsed, or when nothing needed wrapping.
    /// </summary>
    internal static string Rewrite(string json, string launcherPath, string steamExePath, out int rewritten)
    {
        rewritten = 0;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root?["apps"] is not JsonArray apps) return json;

        foreach (var app in apps)
        {
            if (app is not JsonObject entry) continue;
            if (!TryBuildCommand(entry, launcherPath, steamExePath, out var command, out var fromDetached))
                continue;

            entry["cmd"] = command;
            if (fromDetached) RemoveSteamUrls(entry);
            BlankSteamPrepCommands(entry);
            rewritten++;
        }

        return rewritten == 0
            ? json
            : root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool TryBuildCommand(JsonObject entry, string launcher, string steamExe,
        out string command, out bool fromDetached)
    {
        command = "";
        fromDetached = false;

        var cmd = (entry["cmd"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : null;
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            var match = SteamCommand.Match(cmd);
            if (!match.Success) return false;

            var exe = match.Groups["exe"].Value;
            var hasDirectory = exe.IndexOfAny(['\\', '/']) >= 0;
            if (hasDirectory && !Path.IsPathFullyQualified(exe)) return false;
            command = Wrap(launcher, hasDirectory ? exe : steamExe, match.Groups["rest"].Value.Trim());
            return true;
        }

        // No cmd: a steam:// link in detached is how the stock "Steam Big Picture" entry starts.
        if (entry["detached"] is not JsonArray detached) return false;
        foreach (var item in detached)
        {
            var text = (item as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;
            if (text is null || !text.TrimStart().StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
                continue;
            command = Wrap(launcher, steamExe, text.Trim());
            fromDetached = true;
            return true;
        }
        return false;
    }

    private static string Wrap(string launcher, string steamExe, string arguments)
    {
        var text = $"\"{launcher}\" --steam \"{steamExe}\" --";
        return arguments.Length == 0 ? text : text + " " + arguments;
    }

    private static bool IsSteamUrl(JsonNode? node) =>
        (node as JsonValue)?.TryGetValue<string>(out var t) == true &&
        t.TrimStart().StartsWith("steam://", StringComparison.OrdinalIgnoreCase);

    private static void RemoveSteamUrls(JsonObject entry)
    {
        if (entry["detached"] is not JsonArray detached) return;
        for (var i = detached.Count - 1; i >= 0; i--)
            if (IsSteamUrl(detached[i])) detached.RemoveAt(i);
        if (detached.Count == 0) entry.Remove("detached");
    }

    private static void BlankSteamPrepCommands(JsonObject entry)
    {
        if (entry["prep-cmd"] is not JsonArray prep) return;
        foreach (var step in prep)
        {
            if (step is not JsonObject obj) continue;
            foreach (var key in new[] { "do", "undo" })
                if (IsSteamUrl(obj[key])) obj[key] = "";
        }
    }
}
