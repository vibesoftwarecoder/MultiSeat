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
///
/// Isolation fails closed. A Steam entry that cannot be wrapped (the launcher, hook or Steam is
/// missing, the path is relative, or there is more than one link to carry) is removed from the
/// list and reported, never left to start Steam without the hook.
/// </summary>
internal static class SteamAppListRewriter
{
    // Either the bare name (steam, steam.exe), or a path that ends in steam or steam.exe. The
    // bare name is tried first, and the path prefix is lazy, so the exe ends at the first place a
    // steam.exe can end: "steam.exe -applaunch 1 D:\maps\steam" is the bare name plus arguments,
    // not one long path. A directory part may hold spaces, so an unquoted
    // "C:\Program Files (x86)\Steam\steam.exe" works. Anything else is not Steam.
    private static readonly Regex SteamCommand = new(
        @"^\s*""?(?<exe>steam(?:\.exe)?|[^""]*?[\\/]steam(?:\.exe)?)""?(?:\s+(?<rest>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>A Steam entry that was removed instead of wrapped, and why.</summary>
    internal sealed record Refusal(string App, string Reason);

    /// <summary>
    /// Returns the app list with Steam entries wrapped. <paramref name="checkSteam"/> is asked,
    /// for each Steam entry, whether the Steam executable it would run can be isolated, and returns
    /// the reason when it cannot. An entry that cannot is removed and listed in
    /// <paramref name="refused"/>. The text is returned unchanged when it cannot be parsed, or when
    /// nothing was wrapped or removed.
    /// </summary>
    internal static string Rewrite(string json, string launcherPath, string steamExePath,
        Func<string, string?> checkSteam, out int rewritten, out IReadOnlyList<Refusal> refused)
    {
        rewritten = 0;
        var refusals = new List<Refusal>();
        refused = refusals;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root?["apps"] is not JsonArray apps) return json;

        for (var i = apps.Count - 1; i >= 0; i--)
        {
            if (apps[i] is not JsonObject entry) continue;
            if (!TryClassify(entry, steamExePath, out var steam)) continue;

            var reason = steam.Problem ?? checkSteam(steam.Exe);
            if (reason is not null)
            {
                refusals.Add(new Refusal((entry["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : "(unnamed)", reason));
                apps.RemoveAt(i);
                continue;
            }

            entry["cmd"] = Wrap(launcherPath, steam.Exe, steam.Arguments);
            if (steam.FromDetached) RemoveSteamUrls(entry);
            BlankSteamPrepCommands(entry);
            rewritten++;
        }
        refusals.Reverse(); // back in list order

        return rewritten == 0 && refusals.Count == 0
            ? json
            : root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>What a Steam entry would run. <see cref="Problem"/> is set when it cannot be wrapped at all.</summary>
    private readonly record struct SteamEntry(string Exe, string Arguments, bool FromDetached, string? Problem);

    private static bool TryClassify(JsonObject entry, string steamExe, out SteamEntry steam)
    {
        steam = default;

        var cmd = (entry["cmd"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : null;
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            var match = SteamCommand.Match(cmd);
            if (!match.Success) return false;

            var exe = match.Groups["exe"].Value;
            var hasDirectory = exe.IndexOfAny(['\\', '/']) >= 0;
            var problem = hasDirectory && !Path.IsPathFullyQualified(exe)
                ? $"'{exe}' is a relative path, so the Steam it starts cannot be known"
                : null;
            steam = new SteamEntry(hasDirectory ? exe : steamExe, match.Groups["rest"].Value.Trim(), false, problem);
            return true;
        }

        // No cmd: a steam:// link in detached is how the stock "Steam Big Picture" entry starts.
        if (entry["detached"] is not JsonArray detached) return false;
        var links = detached.Where(IsSteamUrl).Select(item => (string)item!).ToList();
        if (links.Count == 0) return false;

        // cmd can carry one link. Wrapping the first and dropping the rest would lose work
        // without a word, so an entry with several is refused instead.
        steam = new SteamEntry(steamExe, links[0].Trim(), true,
            links.Count > 1 ? $"it has {links.Count} steam:// links in detached, and the launcher can carry only one" : null);
        return true;
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
