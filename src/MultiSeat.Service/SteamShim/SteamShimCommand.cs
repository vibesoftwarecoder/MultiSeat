using MultiSeat.Service.Sessions;

namespace MultiSeat.Service.SteamShim;

/// <summary>
/// Reads and writes the command lines that start Steam: the <c>steam://</c> handler, the Run entry,
/// and shortcut targets. Pure string work, so it is tested without a registry or a shortcut file.
///
/// The shim's own syntax is <c>"shim.exe" --steam "steam.exe" -- &lt;the arguments steam.exe was given&gt;</c>.
/// </summary>
internal static class SteamShimCommand
{
    /// <summary>
    /// Splits a command line that starts <c>steam.exe</c> into the executable and the rest.
    /// False for anything that is not Steam, a relative path, or a name Windows would not resolve to
    /// <c>steam.exe</c>. <c>steam.exe.</c> and <c>steam.exe::$DATA</c> do start Steam, so they count
    /// (see <see cref="SteamIsolationLaunchPlan.TryNormalizeSteamPath"/>).
    /// </summary>
    internal static bool TryParseSteam(string? command, out string exe, out string rest)
    {
        exe = rest = "";
        if (string.IsNullOrWhiteSpace(command)) return false;
        var text = command.TrimStart();

        string candidate;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) return false;
            candidate = text[1..close];
            rest = text[(close + 1)..];
        }
        else
        {
            // Unquoted, and a path may contain spaces: the program is the text up to "steam.exe".
            var at = text.IndexOf("steam.exe", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            var end = at + "steam.exe".Length;
            candidate = text[..end];
            rest = text[end..];
        }

        if (!SteamIsolationLaunchPlan.TryNormalizeSteamPath(candidate, out var normalized) ||
            candidate.IndexOfAny(['"', '\0']) >= 0 || !Path.IsPathFullyQualified(normalized))
        {
            exe = rest = "";
            return false;
        }

        exe = normalized;
        rest = rest.Trim();
        return true;
    }

    /// <summary>
    /// The arguments part of the shim's command line, for a shortcut whose target is the shim:
    /// <c>--steam "steam.exe" -- original arguments</c>.
    /// </summary>
    internal static string ShimArguments(string steamExe, string originalArguments) =>
        $"--steam \"{steamExe}\" --" + (string.IsNullOrWhiteSpace(originalArguments) ? "" : " " + originalArguments.Trim());

    /// <summary>The whole command line, for the registry: <c>"shim.exe" --steam "steam.exe" -- ...</c>.</summary>
    internal static string ShimCommand(string shimPath, string steamExe, string originalArguments) =>
        $"\"{shimPath}\" " + ShimArguments(steamExe, originalArguments);

    /// <summary>True when the command line already starts the shim, whatever its arguments.</summary>
    internal static bool StartsShim(string? command, string shimPath)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var text = command.TrimStart();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 0 && string.Equals(text[1..close], shimPath, StringComparison.OrdinalIgnoreCase);
        }

        return text.StartsWith(shimPath, StringComparison.OrdinalIgnoreCase) &&
               (text.Length == shimPath.Length || char.IsWhiteSpace(text[shimPath.Length]));
    }

    /// <summary>
    /// The icon a shortcut should show once its target is the shim. A shortcut with no icon of its own
    /// shows its target's, and the target is about to change, so the icon is pinned to Steam's.
    /// </summary>
    internal static string PinnedIcon(string iconLocation, string steamExe) =>
        string.IsNullOrWhiteSpace(iconLocation) || iconLocation.TrimStart().StartsWith(',')
            ? $"{steamExe},{IconIndex(iconLocation)}"
            : iconLocation;

    private static string IconIndex(string iconLocation)
    {
        var comma = iconLocation?.LastIndexOf(',') ?? -1;
        return comma >= 0 && int.TryParse(iconLocation![(comma + 1)..], out var index) ? index.ToString() : "0";
    }
}
