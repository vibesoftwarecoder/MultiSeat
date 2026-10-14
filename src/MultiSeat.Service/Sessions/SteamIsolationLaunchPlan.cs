using System.Reflection.PortableExecutable;
using MultiSeat.Service.Configuration;

namespace MultiSeat.Service.Sessions;

/// <summary>Wraps explicit Steam launches without changing unrelated seat processes.</summary>
internal sealed record SteamIsolationLaunchPlan(string ExecutablePath, string? Arguments, bool Supervised)
{
    private string? SteamPath { get; init; }
    private string? SteamArguments { get; init; }

    internal SteamIsolationLaunchPlan WithReadyEvent(string eventName)
    {
        if (!Supervised) throw new InvalidOperationException("Only isolated Steam launches use a readiness event.");
        if (!eventName.StartsWith(@"Global\MultiSeatSteamReady-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(eventName[@"Global\MultiSeatSteamReady-".Length..], "N", out _))
            throw new ArgumentException("Invalid Steam readiness event name.", nameof(eventName));
        return this with { Arguments = BuildArguments(SteamPath!, SteamArguments, eventName) };
    }

    internal static SteamIsolationLaunchPlan Create(string executablePath, string? arguments,
        MultiSeatOptions options)
    {
        if (!options.SteamIsolationEnabled || !TryNormalizeSteamPath(executablePath, out var steamPath))
            return new(executablePath, arguments, false);

        // From here on the normalized path is the only one used: it is what the launcher is told
        // to start and what is checked on disk, so the name Windows would resolve is the name
        // that gets wrapped.
        executablePath = steamPath;
        RequireFile(executablePath, "Steam executable");
        var launcher = options.SteamIsolationLauncherPath;
        RequireFile(launcher, "Steam isolation launcher");
        var directory = Path.GetDirectoryName(launcher)!;
        var machine = ReadMachine(executablePath);
        if (machine is not (Machine.I386 or Machine.Amd64))
            throw new InvalidOperationException("Steam isolation supports only x86 and x64 Steam executables.");

        RequireMachine(launcher, Machine.Amd64, "Steam isolation launcher");
        var hook = Path.Combine(directory, machine == Machine.I386
            ? "MultiSeat.SteamHook32.dll" : "MultiSeat.SteamHook.dll");
        RequireFile(hook, "Steam isolation hook");
        RequireMachine(hook, machine, "Steam isolation hook");
        if (machine == Machine.I386)
        {
            var launcher32 = Path.Combine(directory, "MultiSeat.SteamLauncher32.exe");
            RequireFile(launcher32, "32-bit Steam isolation launcher");
            RequireMachine(launcher32, Machine.I386, "32-bit Steam isolation launcher");
        }

        // Steam arguments are already a Windows command-line tail. Do not split/requote them.
        return new(launcher, BuildArguments(executablePath, arguments, null), true)
        { SteamPath = executablePath, SteamArguments = arguments };
    }

    /// <summary>
    /// True when Windows would start <c>steam.exe</c> for this path, with the path rewritten to the
    /// name it resolves to. Comparing the raw file name is not enough: Windows drops trailing dots
    /// and spaces from the last path segment, and a <c>:stream</c> suffix names a stream of the same
    /// file (only the default stream; a named one is not Steam), so <c>steam.exe.</c>,
    /// <c>steam.exe </c> and <c>steam.exe::$DATA</c> all start Steam and
    /// would otherwise be launched unwrapped, with no error. Only the last segment is touched;
    /// the directory part and the name's own casing are kept.
    /// </summary>
    internal static bool TryNormalizeSteamPath(string path, out string normalized)
    {
        normalized = path;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var prefixLength = path.LastIndexOfAny(['\\', '/']) + 1;
        // A drive-relative path ("C:steam.exe") has no separator, but its drive prefix is not
        // part of the name and must not be mistaken for a stream suffix.
        if (prefixLength == 0 && path.Length >= 2 && path[1] == ':') prefixLength = 2;

        var name = path[prefixLength..];
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            // Only the default data stream is the file itself. A named stream is something else.
            var stream = name[(colon + 1)..];
            if (stream.Length != 0 &&
                !stream.Equals(":$DATA", StringComparison.OrdinalIgnoreCase) &&
                !stream.Equals("$DATA", StringComparison.OrdinalIgnoreCase))
                return false;
            name = name[..colon];
        }
        name = name.TrimEnd('.', ' ');

        if (!string.Equals(name, "steam.exe", StringComparison.OrdinalIgnoreCase)) return false;
        normalized = path[..prefixLength] + name;
        return true;
    }

    private static string BuildArguments(string steam, string? arguments, string? readyEvent)
    {
        var wrapped = $"--steam {QuotePath(steam)}";
        if (readyEvent is not null) wrapped += $" --ready-event {readyEvent}";
        wrapped += " --";
        if (!string.IsNullOrWhiteSpace(arguments)) wrapped += " " + arguments;
        return wrapped;
    }

    private static void RequireFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['"', '\0']) >= 0 ||
            !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException(
                $"{description} must be an existing absolute file path: '{path}'. " +
                "Steam was not launched without isolation.");
    }

    private static Machine ReadMachine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.PEHeader is null) throw new BadImageFormatException();
            return pe.PEHeaders.CoffHeader.Machine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            throw new InvalidOperationException($"Cannot read Steam isolation executable architecture: '{path}'.", ex);
        }
    }

    private static void RequireMachine(string path, Machine expected, string description)
    {
        if (ReadMachine(path) != expected)
            throw new InvalidOperationException($"{description} has the wrong architecture: '{path}' (expected {expected}).");
    }

    // Validated file paths cannot contain a quote and cannot end in a directory separator.
    private static string QuotePath(string path) => $"\"{path}\"";
}
