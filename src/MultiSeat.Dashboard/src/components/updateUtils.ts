import type { UpdateComponent, UpdateStatus, UpdatesState } from "../api/types";

// Helpers for the update notices. Kept free of React so they are easy to test.

export const DISMISS_KEY = "multiseat-update-dismissed";

/** At most this many announcement lines are shown at once. */
export const MAX_ANNOUNCEMENTS = 3;

/** Release links are only followed when the service-built URL points into our own GitHub account. */
const RELEASE_URL_PREFIX = "https://github.com/vibesoftwarecoder/";

/**
 * Returns the URL when it is safe to put in an href, otherwise null.
 * The service builds this URL from a fixed repo name and the parsed tag, so a mismatch means
 * something is wrong, and the safe answer is to show no link at all.
 */
export function safeReleaseUrl(url: unknown): string | null {
  if (typeof url !== "string" || !url.startsWith(RELEASE_URL_PREFIX)) return null;
  try {
    const parsed = new URL(url);
    if (
      parsed.protocol !== "https:" ||
      parsed.hostname !== "github.com" ||
      parsed.username !== "" ||
      parsed.password !== "" ||
      !parsed.pathname.startsWith("/vibesoftwarecoder/")
    ) {
      return null;
    }
  } catch {
    return null;
  }
  return url;
}

/** Dismissed versions per component id. Storage can be blocked or throw, so any failure is an empty map. */
export function loadDismissed(): Record<string, string> {
  try {
    const raw = localStorage.getItem(DISMISS_KEY);
    if (!raw) return {};
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return {};
    const out: Record<string, string> = {};
    for (const [id, version] of Object.entries(parsed)) {
      if (typeof version === "string") out[id] = version;
    }
    return out;
  } catch {
    return {};
  }
}

/** Best effort. A failed write only means the notice comes back on the next page load. */
export function saveDismissed(map: Record<string, string>): void {
  try {
    localStorage.setItem(DISMISS_KEY, JSON.stringify(map));
  } catch {
    // Storage blocked or full: the in-memory state still hides the notice for this visit.
  }
}

/** The components whose banner line should show: announced, with a latest version, not dismissed for that version. */
export function visibleAnnouncements(
  state: UpdatesState | null,
  dismissed: Record<string, string>
): UpdateComponent[] {
  if (!state || !Array.isArray(state.components)) return [];
  return state.components
    .filter((c) => c.announce && c.latest != null && dismissed[c.id] !== c.latest.version)
    .slice(0, MAX_ANNOUNCEMENTS);
}

/** "just now", "5 min ago", "2 h ago", "3 d ago"; null or unparseable input gives "not yet". */
export function formatAge(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return "not yet";
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return "not yet";
  const minutes = Math.floor((now - then) / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.floor(hours / 24)} d ago`;
}

/** Short status text for the System page card. None of these may call an unidentified build outdated or available. */
export function statusLabel(status: UpdateStatus): string {
  switch (status) {
    case "upToDate":
      return "Up to date";
    case "updateAvailable":
      return "Update available";
    case "ahead":
      return "Newer than the latest release";
    case "unknownInstalled":
      return "Cannot compare";
    case "notInstalled":
      return "Not found on this host";
    case "latestOnly":
      return "Not tied to a specific client";
    case "unavailable":
      return "No data yet";
    case "disabled":
      return "Update checks off";
    default:
      return "Unknown";
  }
}

/** The one-line banner wording for an announced component (design section 7.3). Plain text only. */
export function announcementText(c: UpdateComponent): string {
  const latest = c.latest;
  if (!latest) return `${c.name}: a newer release exists.`;
  if (c.status === "latestOnly") {
    return `New ${c.name} release ${latest.version} for your client devices. MultiSeat cannot see which version they run.`;
  }
  if (c.status === "unknownInstalled") {
    return `${c.name} ${latest.tag} is available. This host's build could not be identified, so it may already be current.`;
  }
  if (c.installed) {
    return `${c.name} ${latest.version} is available. You have ${c.installed.version}.`;
  }
  return `${c.name} ${latest.version} is available.`;
}
