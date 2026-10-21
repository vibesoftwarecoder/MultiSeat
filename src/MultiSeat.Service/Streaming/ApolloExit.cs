namespace MultiSeat.Service.Streaming;

/// <summary>Why a seat's Apollo exited, as far as its exit code tells (issue #124).</summary>
internal enum ApolloExitKind
{
    /// <summary>Quit from Apollo's web UI or tray. The user wants it to stay stopped.</summary>
    Quit,

    /// <summary>Restart from Apollo's web UI or tray. Apollo expects its supervisor to relaunch it.</summary>
    Restart,

    /// <summary>Anything else, including an exit code we could not read or a process we killed.</summary>
    Crash,
}

/// <summary>What the health check does about a seat's Apollo that is no longer running.</summary>
internal enum ApolloExitAction
{
    /// <summary>Do not relaunch. Leave Apollo stopped until an explicit start, restart or reprovision.</summary>
    LeaveStopped,

    /// <summary>Relaunch without spending one of the MaxRestartAttempts.</summary>
    RelaunchUncounted,

    /// <summary>Relaunch and count it as a crash restart, as every relaunch was before #124.</summary>
    RelaunchCounted,
}

/// <summary>
/// Reads a seat Apollo's exit code (issue #124).
///
/// This only works because seat Apollo runs with no console window (CREATE_NO_WINDOW, see
/// ProcessInjector.CreationFlagsFor). Apollo then treats itself as supervised, and its exit
/// codes mean something:
///   - Quit (confighttp.cpp quit, system_tray.cpp) exits ERROR_SHUTDOWN_IN_PROGRESS, 1115.
///   - Restart (platf::restart in platform/windows/misc.cpp) exits 0 and starts nothing.
/// With a console, as before #124, Quit exited 0 too and Restart started a copy of itself
/// before exiting 0, so neither could be told apart from the outside.
/// </summary>
internal static class ApolloExit
{
    /// <summary>ERROR_SHUTDOWN_IN_PROGRESS: Apollo's "do not respawn me".</summary>
    internal const int QuitExitCode = 1115;

    /// <summary>A clean exit: what Apollo returns after a Restart request.</summary>
    internal const int RestartExitCode = 0;

    /// <param name="exitCode">The exit code, or null when it is unknown.</param>
    internal static ApolloExitKind Classify(int? exitCode) => exitCode switch
    {
        QuitExitCode => ApolloExitKind.Quit,
        RestartExitCode => ApolloExitKind.Restart,
        _ => ApolloExitKind.Crash,
    };

    /// <summary>
    /// What to do about an exit of this kind.
    ///
    /// A Restart is only free when the instance had already outlived its startup window
    /// (<paramref name="isEarlyExit"/> false). An Apollo that exits 0 on its own straight after
    /// every launch would otherwise be relaunched every health check forever. Counted, it stops
    /// after MaxRestartAttempts like any other crash loop. A user who clicks Restart within the
    /// first 30 seconds of a launch spends one attempt; that is the cost of the bound.
    /// </summary>
    internal static ApolloExitAction Decide(ApolloExitKind kind, bool isEarlyExit) => kind switch
    {
        ApolloExitKind.Quit => ApolloExitAction.LeaveStopped,
        ApolloExitKind.Restart when !isEarlyExit => ApolloExitAction.RelaunchUncounted,
        _ => ApolloExitAction.RelaunchCounted,
    };
}
