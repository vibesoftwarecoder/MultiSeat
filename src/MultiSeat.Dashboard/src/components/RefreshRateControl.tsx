import { useEffect, useState } from "react";
import { system } from "../api/client";
import type { RefreshRateStatus } from "../api/types";
import { REFRESH_RATE_CHOICES, hzForInterval, refreshRateLabel } from "./refreshRate";

/**
 * The host-wide refresh-rate setting (issue #74), through /api/system/refresh-rate.
 *
 * One value caps every seat on the host. The service writes it to the registry at once and
 * saves it to appsettings.local.json, but Windows reads it only when a seat's session starts,
 * so a running seat keeps its current rate.
 */
export function RefreshRateControl() {
  const [status, setStatus] = useState<RefreshRateStatus | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  useEffect(() => {
    system
      .getRefreshRate()
      .then(setStatus)
      .catch((e) => setLoadError(e instanceof Error ? e.message : "Failed to load"));
  }, []);

  // Clear the success note after a few seconds, as ResizeControl does.
  useEffect(() => {
    if (!done) return;
    const t = setTimeout(() => setDone(null), 8000);
    return () => clearTimeout(t);
  }, [done]);

  const handleChange = async (intervalMs: number) => {
    if (!status || pending || intervalMs === status.intervalMs) return;
    setPending(true);
    setError(null);
    setDone(null);
    try {
      const r = await system.setRefreshRate(intervalMs);
      setStatus({ ...status, intervalMs: r.intervalMs, refreshRateHz: r.refreshRateHz, registryIntervalMs: r.intervalMs });
      if (r.persistError) {
        setError(
          `Applied, but not saved to the config file, so it reverts when the service restarts: ${r.persistError}`
        );
      }
      setDone(`Now ${r.refreshRateHz} fps. Seats started from now on get it.`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to change the refresh rate");
    } finally {
      setPending(false);
    }
  };

  if (loadError) {
    return (
      <div className="card">
        <div className="card-body">
          <h3>Refresh Rate</h3>
          <div className="error-banner">{loadError}</div>
        </div>
      </div>
    );
  }
  if (!status) return null;

  const isKnown = REFRESH_RATE_CHOICES.some((c) => c.intervalMs === status.intervalMs);
  const registryDiffers =
    status.registryIntervalMs !== null && status.registryIntervalMs !== status.intervalMs;

  return (
    <div className="card">
      <div className="card-body">
        <h3>Refresh Rate</h3>
        <div className="stat-grid" style={{ marginBottom: 12 }}>
          <div className="stat-item">
            <span className="stat-label">Current</span>
            <span className="stat-value">
              {status.refreshRateHz} fps ({status.intervalMs}ms)
            </span>
          </div>
        </div>
        <select
          value={status.intervalMs}
          onChange={(e) => handleChange(Number(e.target.value))}
          disabled={pending}
          aria-label="Host refresh rate"
        >
          {!isKnown && (
            // A value set by hand in the config file. Shown so the dropdown tells the truth, but
            // it cannot be chosen again from here: the service accepts only measured values.
            <option value={status.intervalMs} disabled>
              {refreshRateLabel(status.intervalMs)}
            </option>
          )}
          {REFRESH_RATE_CHOICES.map((c) => (
            <option key={c.intervalMs} value={c.intervalMs}>
              {c.label}
            </option>
          ))}
        </select>
        <div className="text-muted" style={{ fontSize: 12, marginTop: 8 }}>
          Shared by every seat on this host. Applies to each seat's next session. A running seat
          keeps its current rate until it is stopped and started again.
        </div>
        {registryDiffers && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 6, color: "var(--warning)" }}>
            Windows currently holds {status.registryIntervalMs}ms (
            {hzForInterval(status.registryIntervalMs!)} fps), so new seats get that instead.
          </div>
        )}
        {pending && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 6 }}>
            Saving...
          </div>
        )}
        {done && <div className="field-success">{done}</div>}
        {error && <div className="error-banner">{error}</div>}
      </div>
    </div>
  );
}
