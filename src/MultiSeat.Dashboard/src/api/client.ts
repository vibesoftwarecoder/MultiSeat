import type {
  SeatInfo,
  SeatPreset,
  SeatRequest,
  LaunchAppRequest,
  AccountInfo,
  AccountCreateRequest,
  SystemStatus,
  ApiAuthStatus,
  RefreshRateStatus,
  RefreshRateChange,
  ControllerInfo,
  ControllerAssignments,
  HookStatus,
  InputMode,
  SeatServices,
  NvencQualityPreset,
  HostApolloInfo,
  ScaleChange,
  UpdatesState,
} from "./types";

const BASE = "/api";

/** Status carried by the ApiError thrown when a request with a timeout ran out of time. */
export const REQUEST_TIMED_OUT = 0;

/**
 * The service runs a manual update check inside its request (about 30 s at most), so that one
 * call gets a client-side limit. Other calls pass no timeout and behave exactly as before.
 */
export const CHECK_TIMEOUT_MS = 45_000;

async function request<T>(
  path: string,
  init?: RequestInit,
  timeoutMs?: number
): Promise<T> {
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
  };

  // API key from localStorage (set in Settings)
  const apiKey = localStorage.getItem("multiseat-api-key");
  if (apiKey) {
    headers["X-MultiSeat-Key"] = apiKey;
  }

  const controller = timeoutMs ? new AbortController() : undefined;
  const timer = controller ? setTimeout(() => controller.abort(), timeoutMs) : undefined;

  try {
    const res = await fetch(`${BASE}${path}`, {
      ...init,
      headers: { ...headers, ...init?.headers },
      ...(controller ? { signal: controller.signal } : {}),
    });

    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      throw new ApiError(res.status, body?.error ?? res.statusText);
    }

    if (res.status === 204) return undefined as T;
    return await res.json();
  } catch (e) {
    if (controller?.signal.aborted && !(e instanceof ApiError)) {
      throw new ApiError(REQUEST_TIMED_OUT, "The request timed out");
    }
    throw e;
  } finally {
    clearTimeout(timer);
  }
}

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string
  ) {
    super(message);
    this.name = "ApiError";
  }
}

// ── Seats ─────────────────────────────────────────────────────────

export const seats = {
  list: () => request<SeatInfo[]>("/seats"),

  get: (id: string) => request<SeatInfo>(`/seats/${id}`),

  create: (req: SeatRequest) =>
    request<SeatInfo>("/seats", {
      method: "POST",
      body: JSON.stringify(req),
    }),

  launch: (id: string, req: LaunchAppRequest) =>
    request<{ status: string }>(`/seats/${id}/launch`, {
      method: "POST",
      body: JSON.stringify(req),
    }),

  destroy: (id: string) =>
    request<void>(`/seats/${id}`, { method: "DELETE" }),

  services: (id: string) =>
    request<SeatServices>(`/seats/${id}/services`),

  apolloStop: (id: string) =>
    request<{ status: string }>(`/seats/${id}/apollo/stop`, { method: "POST" }),

  apolloStart: (id: string) =>
    request<{ status: string }>(`/seats/${id}/apollo/start`, { method: "POST" }),

  apolloRestart: (id: string) =>
    request<{ status: string }>(`/seats/${id}/apollo/restart`, { method: "POST" }),

  resetAudio: (id: string) =>
    request<{ status: string }>(`/seats/${id}/audio/reset`, { method: "POST" }),

  resetDisplay: (id: string) =>
    request<{ status: string }>(`/seats/${id}/display/reset`, { method: "POST" }),

  resetController: (id: string) =>
    request<{ status: string }>(`/seats/${id}/controller/reset`, { method: "POST" }),

  sessionReconnect: (id: string) =>
    request<void>(`/seats/${id}/session-reconnect`, { method: "POST" }),

  presets: () => request<SeatPreset[]>("/seats/presets"),

  setAutoStart: (id: string, enabled: boolean) =>
    request<{ autoStart: boolean }>(`/seats/${id}/autostart`, {
      method: "PUT",
      body: JSON.stringify({ enabled }),
    }),

  setNvencPreset: (id: string, preset: NvencQualityPreset) =>
    request<{ preset: string }>(`/seats/${id}/nvenc-preset`, {
      method: "POST",
      body: JSON.stringify({ preset }),
    }),

  // Reconnects the seat's session at the new size and restarts its Apollo. The Windows session
  // survives, but a live stream drops for a few seconds; the request returns once it is done.
  setResolution: (id: string, width: number, height: number) =>
    request<{ width: number | null; height: number | null; sessionId: number | null }>(`/seats/${id}/resolution`, {
      method: "POST",
      body: JSON.stringify({ width, height }),
    }),

  // Sets the seat's DPI scale override, or clears it with null. When the scale in effect changes
  // the service reconnects the session as it does for a resize, so a live stream drops briefly.
  setScale: (id: string, scaleFactor: number | null) =>
    request<ScaleChange>(`/seats/${id}/scale`, {
      method: "POST",
      body: JSON.stringify({ scaleFactor }),
    }),

  clients: (id: string) =>
    request<string[]>(`/seats/${id}/clients`),

  unpairClient: (id: string, name: string) =>
    request<void>(`/seats/${id}/clients/${encodeURIComponent(name)}`, { method: "DELETE" }),

  unpairAll: (id: string) =>
    request<void>(`/seats/${id}/clients`, { method: "DELETE" }),
};

// ── Accounts ──────────────────────────────────────────────────────

export const accounts = {
  list: () => request<AccountInfo[]>("/accounts"),

  create: (req: AccountCreateRequest) =>
    request<AccountInfo>("/accounts", {
      method: "POST",
      body: JSON.stringify(req),
    }),

  link: (req: AccountCreateRequest) =>
    request<AccountInfo>("/accounts/link", {
      method: "POST",
      body: JSON.stringify(req),
    }),

  destroy: (username: string) =>
    request<void>(`/accounts/${username}`, { method: "DELETE" }),
};

// ── System ────────────────────────────────────────────────────────

export const system = {
  health: () => request<SystemStatus>("/system/health"),
  rebuild: () => request<{ message: string }>("/system/rebuild", { method: "POST" }),
  getAuth: () => request<ApiAuthStatus>("/system/auth"),
  setAuth: (enabled: boolean) =>
    request<ApiAuthStatus>("/system/auth", {
      method: "POST",
      body: JSON.stringify({ enabled }),
    }),
  getRefreshRate: () => request<RefreshRateStatus>("/system/refresh-rate"),
  setRefreshRate: (intervalMs: number) =>
    request<RefreshRateChange>("/system/refresh-rate", {
      method: "POST",
      body: JSON.stringify({ intervalMs }),
    }),
  // Update notices. GET only reads the service's cache; it never makes the service call GitHub.
  getUpdates: () => request<UpdatesState>("/system/updates"),
  // 202 with the new state; 409 when update checks are off; 429 inside the 60 s cooldown.
  checkUpdates: () =>
    request<UpdatesState>("/system/updates/check", { method: "POST" }, CHECK_TIMEOUT_MS),
  setUpdatesEnabled: (enabled: boolean) =>
    request<UpdatesState>("/system/updates/settings", {
      method: "POST",
      body: JSON.stringify({ enabled }),
    }),
};

// ── Host (the console's own Apollo) ───────────────────────────────

export const host = {
  get: () => request<HostApolloInfo>("/host"),
};

// ── Input ─────────────────────────────────────────────────────────

export const input = {
  controllers: () => request<ControllerInfo[]>("/input/controllers"),

  assignments: () => request<ControllerAssignments>("/input/assignments"),

  assign: (xinputIndex: number, seatId: string) =>
    request<{ status: string }>("/input/assign", {
      method: "POST",
      body: JSON.stringify({ xinputIndex, seatId }),
    }),

  unassign: (xinputIndex: number) =>
    request<{ status: string }>(`/input/assign/${xinputIndex}`, {
      method: "DELETE",
    }),

  autoAssign: () =>
    request<{ status: string; assignments: ControllerAssignments }>(
      "/input/auto-assign",
      { method: "POST" }
    ),

  hookStatus: () => request<HookStatus>("/input/hooks/status"),

  mode: () => request<InputMode>("/input/mode"),
};
