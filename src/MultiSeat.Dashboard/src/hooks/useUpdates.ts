import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, system } from "../api/client";
import type { UpdatesState } from "../api/types";
import { loadDismissed, saveDismissed } from "../components/updateUtils";

/** A cheap local read on the service, so ten minutes is plenty. */
export const UPDATES_POLL_MS = 10 * 60 * 1000;
/** Window-focus reloads are throttled so tabbing back and forth cannot become a request storm. */
const FOCUS_MIN_GAP_MS = 10_000;

export type ActionResult =
  | { ok: true }
  | { ok: false; kind: "off" | "cooldown" | "error"; message: string };

function looksLikeState(value: unknown): value is UpdatesState {
  return (
    typeof value === "object" &&
    value !== null &&
    Array.isArray((value as UpdatesState).components)
  );
}

function toResult(e: unknown): ActionResult {
  if (e instanceof ApiError) {
    if (e.status === 409) {
      return { ok: false, kind: "off", message: "Update checks are off. Turn them on first." };
    }
    if (e.status === 429) {
      return {
        ok: false,
        kind: "cooldown",
        message: "A check ran a moment ago. Try again in a minute.",
      };
    }
    return { ok: false, kind: "error", message: e.message || "The request failed." };
  }
  return { ok: false, kind: "error", message: "Could not reach the service." };
}

/**
 * Loads the update state on mount, on window focus and every ten minutes.
 * A 401 or any other failure clears the data, which hides the banner. There is no error state
 * to show and no retry beyond the normal schedule.
 */
export function useUpdates() {
  const [data, setData] = useState<UpdatesState | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [dismissed, setDismissed] = useState<Record<string, string>>(loadDismissed);
  const alive = useRef(true);
  const lastLoad = useRef(0);

  const load = useCallback(async () => {
    lastLoad.current = Date.now();
    try {
      const result = await system.getUpdates();
      if (alive.current) setData(looksLikeState(result) ? result : null);
    } catch {
      if (alive.current) setData(null);
    } finally {
      if (alive.current) setLoaded(true);
    }
  }, []);

  useEffect(() => {
    alive.current = true;
    void load();
    const timer = setInterval(() => void load(), UPDATES_POLL_MS);
    const onFocus = () => {
      if (Date.now() - lastLoad.current >= FOCUS_MIN_GAP_MS) void load();
    };
    window.addEventListener("focus", onFocus);
    return () => {
      alive.current = false;
      clearInterval(timer);
      window.removeEventListener("focus", onFocus);
    };
  }, [load]);

  const apply = useCallback(
    async (call: () => Promise<UpdatesState>): Promise<ActionResult> => {
      try {
        const result = await call();
        if (alive.current && looksLikeState(result)) setData(result);
        await load();
        return { ok: true };
      } catch (e) {
        return toResult(e);
      }
    },
    [load]
  );

  const checkNow = useCallback(() => apply(() => system.checkUpdates()), [apply]);
  const setEnabled = useCallback(
    (enabled: boolean) => apply(() => system.setUpdatesEnabled(enabled)),
    [apply]
  );

  /** Hides this component's notice until a newer version is announced. */
  const dismiss = useCallback((id: string, version: string) => {
    setDismissed((prev) => {
      const next = { ...prev, [id]: version };
      saveDismissed(next);
      return next;
    });
  }, []);

  return { data, loaded, dismissed, dismiss, refresh: load, checkNow, setEnabled };
}
