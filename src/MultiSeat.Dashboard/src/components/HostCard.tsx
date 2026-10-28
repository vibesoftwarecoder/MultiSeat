import { useCallback, useEffect, useState } from "react";
import type { HostApolloInfo } from "../api/types";
import { host as hostApi } from "../api/client";
import { HostUsbPanel } from "./HostUsbPanel";

/**
 * The console's own Apollo, shown the way a seat is.
 *
 * MultiSeat deliberately leaves this instance alone — different install dir, different port
 * range, never reaped by startup cleanup — which had the side effect of making the one Apollo
 * the operator actually uses the only one they could not see here.
 *
 * Read-only on purpose: this process is not ours to start, stop or reconfigure.
 * The one exception is the USB forwarding panel, which switches a tunnel the host owner already allowed
 * in Apollo itself; see HostUsbPanel.
 */
export function HostCard() {
  const [info, setInfo] = useState<HostApolloInfo | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setInfo(await hostApi.get());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not read host Apollo status");
    }
  }, []);

  useEffect(() => {
    load();
    const t = setInterval(load, 10000);
    return () => clearInterval(t);
  }, [load]);

  if (error) {
    return (
      <div className="card">
        <div className="card-header">
          <h3 style={{ margin: 0 }}>Host</h3>
        </div>
        <div className="card-body">
          <div className="error-banner">{error}</div>
        </div>
      </div>
    );
  }

  if (!info) return null;

  // Three states worth distinguishing: streaming, up and answering, and present but silent.
  const accent = !info.detected
    ? ""
    : info.streaming
      ? "card--streaming"
      : info.reachable
        ? "card--ready"
        : "card--provisioning";

  // Same palette StatusBadge uses for seats, so host and seats read alike.
  const badge = !info.detected
    ? { text: "Not running", color: "#6b7280" }
    : info.streaming
      ? { text: "Streaming", color: "#3b82f6" }
      : info.reachable
        ? { text: "Ready", color: "#10b981" }
        : { text: "No response", color: "#f59e0b" };

  const uptime = info.startedAt ? formatDuration(new Date(info.startedAt)) : null;

  return (
    <div className={`card ${accent}`}>
      <div className="card-header">
        <div>
          <h3 style={{ margin: 0 }}>{info.hostName ?? "Host"}</h3>
          <span className="text-muted" style={{ fontSize: 12 }}>
            console &middot; standalone Apollo
          </span>
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          {info.streaming && <span className="live-badge">● CONNECTED</span>}
          <span
            style={{
              display: "inline-flex",
              alignItems: "center",
              padding: "2px 10px",
              borderRadius: 12,
              fontSize: 13,
              fontWeight: 600,
              color: "#fff",
              backgroundColor: badge.color,
            }}
          >
            {badge.text}
          </span>
        </div>
      </div>

      <div className="card-body">
        <div className="stat-grid">
          <StatItem
            label="Session"
            value={info.consoleSessionId >= 0 ? `#${info.consoleSessionId}` : "--"}
          />
          <StatItem label="Port" value={info.port ? String(info.port) : "--"} />
          <StatItem
            label="Apollo PID"
            value={info.detected ? String(info.processId) : "--"}
          />
          <StatItem label="Version" value={info.appVersion ?? "--"} />
        </div>

        {info.detected && info.port && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 8 }}>
            Moonlight: {window.location.hostname}:{info.port}
            {" · "}
            {describePairing(info.pairedClientCount)}
          </div>
        )}

        {info.webUiPort && info.detected && (
          <div style={{ marginTop: 8 }}>
            <a
              className="btn-link"
              href={`https://${window.location.hostname}:${info.webUiPort}`}
              target="_blank"
              rel="noreferrer"
            >
              Open Apollo web UI →
            </a>
          </div>
        )}

        {info.serviceStatus && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 4 }}>
            ApolloService: {info.serviceStatus}
          </div>
        )}

        {uptime && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 4 }}>
            Uptime: {uptime}
          </div>
        )}

        {info.executablePath && (
          <div className="text-muted" style={{ fontSize: 11, marginTop: 4 }}>
            {info.executablePath}
          </div>
        )}

        {info.note && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 8 }}>
            {info.note}
          </div>
        )}

        {info.detected && <HostUsbPanel />}
      </div>
    </div>
  );
}

/**
 * -1 means Apollo's state file could not be read, which is not the same as "nobody is paired" —
 * say so rather than assert zero.
 */
function describePairing(count: number): string {
  if (count < 0) return "pairing unknown";
  if (count === 0) return "no paired clients";
  return count === 1 ? "1 paired client" : `${count} paired clients`;
}

function StatItem({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat-item">
      <span className="stat-label">{label}</span>
      <span className="stat-value">{value}</span>
    </div>
  );
}

function formatDuration(since: Date): string {
  const ms = Date.now() - since.getTime();
  const secs = Math.floor(ms / 1000);
  if (secs < 60) return `${secs}s`;
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m ${secs % 60}s`;
  const hrs = Math.floor(mins / 60);
  return `${hrs}h ${mins % 60}m`;
}
