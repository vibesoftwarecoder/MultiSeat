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
        if (!options.SteamIsolationEnabled ||
            !string.Equals(Path.GetFileName(executablePath), "steam.exe", StringComparison.OrdinalIgnoreCase))
            return new(executablePath, arguments, false);

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
