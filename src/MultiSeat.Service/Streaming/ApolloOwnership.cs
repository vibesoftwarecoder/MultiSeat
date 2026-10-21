namespace MultiSeat.Service.Streaming;

/// <summary>
/// Decides whether a running Apollo process belongs to MultiSeat.
///
/// This is the promise that lets MultiSeat coexist with a standalone Apollo serving the console
/// player: we never touch a process we did not launch. Both ways of being wrong are expensive and
/// silent — claim a foreign Apollo and startup cleanup kills someone's live stream; disown our own
/// and orphaned instances accumulate holding the seat's ports.
///
/// It lives here because there were TWO copies of it, and they had drifted: the reporting path
/// guarded both of its emptiness checks, while the path that actually KILLS guarded only one.
/// `cmdLine.Contains("")` is true for every process, so an empty ApolloConfigDir made every
/// sunshine.exe on the host — the console player's included — look managed. One implementation,
/// one set of tests.
/// </summary>
internal static class ApolloOwnership
{
    /// <param name="exePath">The process's executable path, as WMI reports it. May be null.</param>
    /// <param name="cmdLine">The process's command line, as WMI reports it. May be null.</param>
    /// <param name="managedExeDir">Directory MultiSeat launches Apollo from.</param>
    /// <param name="managedConfigDir">Root of the per-seat Apollo config directories.</param>
    /// <remarks>
    /// Every emptiness check here is load-bearing. "anything".StartsWith("") and
    /// "anything".Contains("") are both true, so an unset or unresolved directory would claim
    /// every Apollo on the host rather than none. When nothing is known about a process, the
    /// answer is "not ours" — skipping a process we own is recoverable, killing one we do not is
    /// not.
    /// </remarks>
    internal static bool IsMultiSeatManaged(
        string? exePath, string? cmdLine, string? managedExeDir, string? managedConfigDir)
    {
        if (!string.IsNullOrEmpty(exePath) && !string.IsNullOrEmpty(managedExeDir)
            && exePath.StartsWith(managedExeDir, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(cmdLine) && !string.IsNullOrEmpty(managedConfigDir)
            && cmdLine.Contains(managedConfigDir, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// True when <paramref name="cmdLine"/> passes exactly <paramref name="configPath"/> as an
    /// argument: one seat's Apollo, and no other (issue #124).
    ///
    /// Used to find and kill a stray Apollo for one seat before a launch or during a stop. That is
    /// stricter than <see cref="IsMultiSeatManaged"/>, which accepts any MultiSeat Apollo. A
    /// substring test is not enough here: it is what kills, and it must never pick another seat's
    /// Apollo or the console player's.
    ///
    /// The command line is split into arguments the way Windows does, and each argument after the
    /// executable is compared as a whole path, ignoring case. An unknown command line or an empty
    /// config path matches nothing.
    /// </summary>
    internal static bool CommandLineNamesConfig(string? cmdLine, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(cmdLine) || string.IsNullOrWhiteSpace(configPath))
            return false;

        var wanted = NormalizePath(configPath);
        if (wanted is null) return false;

        var args = SplitCommandLine(cmdLine);
        for (var i = 1; i < args.Count; i++)
        {
            var candidate = NormalizePath(args[i]);
            if (candidate is not null && string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>A full path, or null when the text is not a usable path.</summary>
    private static string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Split a Windows command line into arguments with the CommandLineToArgvW rules for quotes
    /// and backslashes: 2n backslashes before a quote are n backslashes and the quote toggles
    /// quoting, 2n+1 are n backslashes and a literal quote, and other backslashes are literal.
    /// </summary>
    internal static List<string> SplitCommandLine(string cmdLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < cmdLine.Length; i++)
        {
            var c = cmdLine[i];
            if (c == '\\')
            {
                var start = i;
                while (i < cmdLine.Length && cmdLine[i] == '\\') i++;
                var count = i - start;
                if (i < cmdLine.Length && cmdLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
                hasToken = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken) args.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken) args.Add(current.ToString());
        return args;
    }
}
