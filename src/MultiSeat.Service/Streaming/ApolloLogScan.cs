using System.Globalization;

namespace MultiSeat.Service.Streaming;

/// <summary>
/// Reads Apollo's own log for the one startup failure that /serverinfo cannot show (issue #96).
/// The HTTP API comes up whether or not capture works, so a seat can be "ready" with no encoder:
/// main.cpp logs "Video failed to find working encoder" and carries on. Apollo re-probes on the
/// next stream launch, so this is a warning about the seat's state, not proof it is broken.
/// </summary>
internal static class ApolloLogScan
{
    internal const string NoEncoderMarker = "Video failed to find working encoder";
    private const int MaxTailBytes = 256 * 1024;

    /// <summary>
    /// First line at or after <paramref name="sinceLocal"/> that reports no working encoder, or
    /// null. Apollo stamps lines "[yyyy-MM-dd HH:mm:ss.fff]: " in local time; lines from earlier
    /// runs of the same log file (or with no readable stamp) never match.
    /// </summary>
    internal static string? FindNoEncoderLine(string logText, DateTime sinceLocal)
    {
        foreach (var line in logText.Split('\n'))
        {
            if (!line.Contains(NoEncoderMarker, StringComparison.Ordinal)) continue;
            if (line.Length < 25 || line[0] != '[') continue;
            if (DateTime.TryParseExact(line.AsSpan(1, 23), "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)
                && stamp >= sinceLocal)
                return line.Trim();
        }
        return null;
    }

    /// <summary>Reads the end of the log without taking a lock Apollo would notice.</summary>
    internal static string ReadTail(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > MaxTailBytes) fs.Seek(-MaxTailBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
