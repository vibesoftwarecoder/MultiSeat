namespace MultiSeat.Shared;

/// <summary>
/// System-wide constants for port allocation, paths, and limits.
/// Tuned for Windows 11 24H2+ (build 26100+).
/// </summary>
public static class Constants
{
    // ── Port allocation ──────────────────────────────────────────────
    // Each seat reserves a block of PortsPerSeat ports starting from the base.
    // Seat 0 → 48100-48129, Seat 1 → 48130-48159, etc.
    // The base intentionally sits ABOVE a stock Apollo's port block (~47979-48010,
    // centered on the Sunshine/Moonlight default 47984) so MultiSeat seats never
    // collide with a standalone Apollo running on the same host. This lets MultiSeat
    // coexist with a user's existing Apollo out of the box. Configurable via
    // MultiSeat:PortBase in appsettings.json.
    public const int PortBase = 48100;
    // A seat's port offsets span -5 (GFE HTTPS) to +26 (RTSP) — a 32-position range — but only
    // 8 of those positions are actually used: {-5,0,1,9,10,11,12,26}. PortsPerSeat=30 is
    // intentionally smaller than the raw 32-span: at 30-port spacing none of the *used* offsets
    // collide between seats, so blocks stay non-overlapping in practice. Verified by
    // StreamingTests.Constants_PortsPerSeat_NoUsedPortCollision — re-check it if the used-offset
    // set changes.
    public const int PortsPerSeat = 30;

    /// <summary>
    /// Architectural ceiling: how many seat port blocks exist (PortAllocator carves exactly this
    /// many). It is NOT the seat limit an operator gets — that is <c>MultiSeat:MaxSeats</c> in
    /// appsettings.json, which SeatManager enforces and defaults to 4. Configuring more than this
    /// many seats would pass that check and then find no port block available.
    /// </summary>
    public const int MaxSeats = 8;

    // ── Port offsets within a seat's block ────────────────────────────
    // Matches Apollo's map_port(N) = sunshine.port + N (from network.cpp).
    public const int OffsetGfeHttps  = -5;  // GFE HTTPS — Moonlight serverinfo/pair/launch
    public const int OffsetGfeHttp   =  0;  // GFE HTTP  — same endpoints, plaintext fallback
    public const int OffsetWebUi     =  1;  // Apollo web UI HTTPS
    public const int OffsetVideo     =  9;  // RTP video stream
    public const int OffsetControl   = 10;  // ENet control channel
    public const int OffsetAudio     = 11;  // RTP audio stream
    public const int OffsetMic       = 12;  // RTP mic stream (stream_mic)
    public const int OffsetRtsp      = 26;  // RTSP session setup (TCP) — Sunshine stock RTSP port (48010 with PortBase=47984)

    // Emulator netplay (not an Apollo offset). Uses a free slot in the seat's 30-port block so
    // each seat gets a unique, collision-free netplay port (seat 0 = 48113, seat 1 = 48143, …).
    public const int OffsetRetroArchNetplay = 13;  // RetroArch netplay host port (TCP)

    // There were two aliases here, OffsetHttps and OffsetHttp, and both said the opposite of what
    // they pointed at: OffsetHttps was the GFE *HTTP* port (0) and OffsetHttp was the web UI, which
    // is *HTTPS* (1). Removed rather than corrected - an alias whose only job is to be a second
    // name for a value is not worth the chance of reading it at 2am and believing it.

    // ── Default paths ────────────────────────────────────────────────
    // MultiSeat installs and manages its OWN Apollo (ApolloVibe) in a dedicated
    // directory, separate from any standalone Apollo a user may run at
    // C:\Program Files\Apollo. This keeps the two installs independent (versions,
    // process isolation) and lets MultiSeat coexist without touching the user's Apollo.
    public const string DefaultApolloPath = @"C:\Program Files\ApolloVibe\sunshine.exe";
    public const string DefaultApolloConfigDir = @"C:\ProgramData\MultiSeat\apollo";
    public const string DefaultMultiSeatConfigPath = @"C:\ProgramData\MultiSeat\multiseat-host.json";

    // ── Update notifications ─────────────────────────────────────────
    // Fixed on purpose and NOT configurable: a setting that could point the update check at
    // another host or repository would turn an update notice into a generic outbound request
    // primitive. See MultiSeat.Service.Updates.
    public const string UpdateApiHost = "api.github.com";
    public const string UpdateRepoOwner = "vibesoftwarecoder";
    public const string UpdateRepoMultiSeat = "MultiSeat";
    public const string UpdateRepoApolloVibe = "ApolloVibe";
    public const string UpdateRepoMoonlightVibe = "MoonlightVibe";
    // Generic on purpose: no version, no host name. GitHub rejects a request with no User-Agent.
    public const string UpdateUserAgent = "MultiSeat-update-check";
    public const string DefaultUpdateStatePath = @"C:\ProgramData\MultiSeat\update-check.json";
    // File an ApolloVibe release package may carry beside sunshine.exe (tag, commit, sunshineSha256).
    public const string ApolloReleaseMarkerFileName = "release.json";
    public const int DefaultUpdateCheckIntervalHours = 12;
    public const int MinUpdateCheckIntervalHours = 1;
    public const int MaxUpdateCheckIntervalHours = 168;

    // ── Account naming ───────────────────────────────────────────────
    public const string AccountPrefix = "MultiSeatSeat";  // e.g., MultiSeatSeat01
    public const string AccountGroup = "Users";

    // ── Windows session ──────────────────────────────────────────────
    public const int SessionConnectTimeoutMs = 15_000;
    public const int ProcessLaunchTimeoutMs = 10_000;

    // ── API ──────────────────────────────────────────────────────────
    public const string ApiKeyHeader = "X-MultiSeat-Key";
    public const int DefaultApiPort = 9550;
}
