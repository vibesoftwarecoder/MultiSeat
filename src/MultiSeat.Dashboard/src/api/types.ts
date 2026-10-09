// Mirrors MultiSeat.Shared.Models — keep in sync with the C# models

export type NvencQualityPreset = "Latency" | "Balanced" | "Quality";

export type SeatStatus =
  | "Idle"
  | "Provisioning"
  | "Configuring"
  | "Connecting"
  | "Ready"
  | "Streaming"
  | "TearingDown"
  | "Error";

export interface SeatInfo {
  id: string;
  accountName: string;
  sessionId: number;
  status: SeatStatus;
  width: number;
  height: number;
  fps: number;
  displayDevicePath: string | null;
  portBase: number;
  apolloProcessId: number;
  audioDeviceId: string | null;
  vacCableIndex: number;
  viGEmControllerIndex: number;
  createdAt: string;
  readyAt: string | null;
  errorMessage: string | null;
  launchApp: string | null;
  autoStart: boolean;
  nvencPreset: NvencQualityPreset;
  provisioningStep: string | null;
  /** DPI scale in effect, in percent. */
  scaleFactor: number;
  /** Where scaleFactor came from: the seat's override, the host default, or the width. */
  scaleFactorSource: ScaleFactorSource;
  /** The seat's own override, or null when it has none. */
  scaleFactorOverride: number | null;
  /**
   * The scale the session was last READ running at, in percent: what Windows applied, where
   * scaleFactor is what was asked for. Null until it has been read since the last (re)connect.
   */
  appliedScaleFactor?: number | null;
  /** True when the session was read running at a different scale than scaleFactor. */
  scaleMismatch?: boolean;
  /**
   * A plain-language note on the last reading: why the session runs at another scale, that it
   * could not be read, or that its system scale lags until the seat user signs out. Null when the
   * reading matches in full or there is none.
   */
  scaleNote?: string | null;
  /**
   * Set when the seat's fps is above what the host's refresh rate can compose, checked when the
   * seat was provisioned (issue #70). Null when there is no mismatch.
   */
  effectiveRefreshRateWarning: string | null;
}

export type ScaleFactorSource = "Derived" | "HostDefault" | "Seat";

export interface SeatPreset {
  id: string;
  accountName: string;
  width: number;
  height: number;
  fps: number;
  autoStart: boolean;
  nvencPreset: NvencQualityPreset;
  createdAt: string;
}

export interface SeatRequest {
  accountName: string;
  width?: number;
  height?: number;
  fps?: number;
  launchApp?: string;
  nvencPreset?: NvencQualityPreset;
  /** DPI scale override in percent. Leave it out to use the host default or the width. */
  scaleFactor?: number;
  /**
   * Bring the seat back by itself after the service or the PC restarts. true saves it, false
   * removes any saved preset for the account, and leaving it out keeps whatever is saved.
   */
  autoStart?: boolean;
}

/** POST /api/seats/{id}/scale. */
export interface ScaleChange {
  scaleFactor: number | null;
  scaleFactorSource: ScaleFactorSource | null;
  scaleFactorOverride: number | null;
  sessionId: number | null;
}

export interface LaunchAppRequest {
  executablePath: string;
  arguments?: string;
  workingDirectory?: string;
}

export interface AccountInfo {
  username: string;
  sid: string | null;
  profilePath: string | null;
  isManaged: boolean;
  createdAt: string;
}

export interface AccountCreateRequest {
  username: string;
  password: string;
}

export interface GpuInfo {
  name: string;
  utilizationPercent: number;
  vramTotalMb: number;
  vramUsedMb: number;
  encoderUtilizationPercent: number;
  activeEncoderSessions: number;
}

export interface CpuInfo {
  name: string;
  utilizationPercent: number;
  logicalCores: number;
}

export interface NetworkInfo {
  primaryAdapter: string;
  bytesReceivedPerSec: number;
  bytesSentPerSec: number;
}

export interface TopProcess {
  pid: number;
  name: string;
  cpuPercent: number;
  memoryMb: number;
  networkConnections: number;
}

export interface DiskInfo {
  name: string;
  label: string;
  totalMb: number;
  freeMb: number;
  usedPercent: number;
}

export type HealthStatus = "Healthy" | "Warning" | "Critical";

export interface HealthScore {
  status: HealthStatus;
  score: number;
  issues: string[];
}

export interface SystemStatus {
  activeSeats: number;
  maxSeats: number;
  gpu: GpuInfo | null;
  cpu: CpuInfo | null;
  network: NetworkInfo | null;
  topProcesses: TopProcess[];
  disks: DiskInfo[];
  health: HealthScore;
  systemMemoryMb: number;
  availableMemoryMb: number;
  windowsBuild: string;
  rdpWrapperActive: boolean;
  timestamp: string;
}

// ── Input ──────────────────────────────────────────────────────────

export interface ControllerInfo {
  xInputIndex: number;
  assignedSeatId: string | null;
  assignedSeatName: string | null;
}

export interface ControllerAssignments {
  [xinputIndex: string]: string; // index → seatId
}

export interface HookStatus {
  installed: boolean;
  targetSessionId: number;
  enabled: boolean;
}

export interface InputMode {
  // False (default): Apollo forwards the Moonlight client's controller natively,
  // so the XInput→seat assignment UI is inert. True: MultiSeat routes physical
  // XInput controllers into seats via ViGEm.
  viGEmControllerEnabled: boolean;
}

// ── API Auth ──────────────────────────────────────────────────────

export interface ApiAuthStatus {
  authEnabled: boolean;
}

/** GET /api/system/refresh-rate (issue #74). */
export interface RefreshRateStatus {
  intervalMs: number;
  refreshRateHz: number;
  /** What Windows holds now; null when absent or unreadable. */
  registryIntervalMs: number | null;
  defaultIntervalMs: number;
  allowedIntervalsMs: number[];
  appliesTo: string;
}

/** POST /api/system/refresh-rate. */
export interface RefreshRateChange {
  intervalMs: number;
  refreshRateHz: number;
  previousIntervalMs: number;
  appliesTo: string;
  persistedTo: string | null;
  persistError: string | null;
}

// ── Per-seat service status ───────────────────────────────────────

export interface SeatServices {
  // The Apollo PROCESS is alive.
  apollo: boolean;
  // Apollo ANSWERED a serverinfo query on the seat's port, i.e. a Moonlight client could
  // actually reach it. Distinct from `apollo` on purpose: a process can be up and not serving
  // (starting, wedged, wrong port), and only this answers "is the seat usable right now?".
  apolloReachable: boolean;
  // Apollo reports a client actively streaming on this seat.
  apolloStreaming: boolean;
  apolloRestarts: number;
  display: boolean;
  audio: boolean;
  controller: boolean;
  // True when MultiSeat manages a ViGEm pad for this seat. When false (default),
  // Apollo forwards the client's controller natively — the UI shows "Native" instead
  // of a down light.
  controllerManaged: boolean;
  // True when MultiSeat assigns this seat a host-side virtual audio cable (AudioMode
  // SharedHost). When false, audio is per-session — the seat's RDP session owns its own
  // "Remote Audio" endpoint, so the UI shows "Session" instead of a down light and offers
  // no Reset (there is no device assignment to reset).
  audioManaged: boolean;
  inputHooks: boolean;
  firewall: boolean;
  session: boolean;
}

/**
 * The host's own standalone Apollo — the instance run for the console account, which
 * MultiSeat coexists with and never manages. Mirrors MultiSeat.Shared.Models.HostApolloInfo.
 */
export interface HostApolloInfo {
  detected: boolean;
  processId: number;
  executablePath: string | null;
  startedAt: string | null;
  port: number | null;
  webUiPort: number | null;
  reachable: boolean;
  hostName: string | null;
  appVersion: string | null;
  streaming: boolean;
  // How many clients are paired, from Apollo's state file. NOT from serverinfo, whose PairStatus
  // is answered relative to the asking client's uniqueid — a probe with its own id is always told
  // "not paired". -1 means the state file could not be read.
  pairedClientCount: number;
  serviceStatus: string | null;
  consoleSessionId: number;
  note: string | null;
}

// ── Update notifications (GET /api/system/updates) ───────────────
// Everything in here that is text comes from the service, which got it from GitHub. Treat it
// as untrusted: render it as plain React text and never as HTML.

export type UpdateStatus =
  | "upToDate"
  | "updateAvailable"
  | "ahead"
  | "unknownInstalled"
  | "notInstalled"
  | "latestOnly"
  | "unavailable"
  | "disabled";

export type UpdateInstalledSource =
  | "assembly"
  | "marker"
  | "marker-modified"
  | "release-hash"
  | "commit-match";

export type UpdateComponentId = "multiseat" | "apollovibe" | "moonlightvibe";

export interface UpdateInstalled {
  version: string;
  display: string;
  source: UpdateInstalledSource;
  note: string | null;
}

export interface UpdateLatest {
  version: string;
  tag: string;
  publishedAt: string;
  releaseUrl: string;
}

export interface UpdateComponent {
  id: UpdateComponentId | string;
  name: string;
  status: UpdateStatus;
  installed: UpdateInstalled | null;
  installedNote?: string | null;
  latest: UpdateLatest | null;
  announce: boolean;
  checkedAt: string | null;
  error: string | null;
}

export interface UpdatesState {
  enabled: boolean;
  intervalHours: number;
  checkedAt: string | null;
  nextCheckAt: string | null;
  error: string | null;
  components: UpdateComponent[];
}
