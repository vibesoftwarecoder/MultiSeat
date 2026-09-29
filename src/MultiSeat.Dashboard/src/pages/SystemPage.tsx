import { useCallback, useEffect, useState } from "react";
import { system, input } from "../api/client";
import { usePolling } from "../hooks/usePolling";
import type { HookStatus, TopProcess, DiskInfo } from "../api/types";
import { RefreshRateControl } from "../components/RefreshRateControl";

export function SystemPage() {
  const [rebuilding, setRebuilding] = useState(false);
  const [rebuildMsg, setRebuildMsg] = useState<string | null>(null);
  const [authEnabled, setAuthEnabled] = useState<boolean | null>(null);
  const [authToggling, setAuthToggling] = useState(false);

  useEffect(() => {
    system.getAuth().then(r => setAuthEnabled(r.authEnabled)).catch(() => {});
  }, []);

  const handleToggleAuth = async () => {
    const newState = !authEnabled;
    const action = newState ? "Enable" : "Disable";
    if (!confirm(`${action} API authentication?\n\n${newState ? "The dashboard will require an API key." : "The dashboard will be open to anyone on the network."}`)) return;
    setAuthToggling(true);
    try {
      const r = await system.setAuth(newState);
      setAuthEnabled(r.authEnabled);
    } catch (e) {
      alert(e instanceof Error ? e.message : "Failed to update auth setting");
    } finally {
      setAuthToggling(false);
    }
  };

  const healthFetcher = useCallback(() => system.health(), []);
  const hookFetcher = useCallback(() => input.hookStatus().catch(() => null), []);

  const { data: health, loading, error } = usePolling(healthFetcher, 5000);
  const { data: hookStatus } = usePolling<HookStatus | null>(hookFetcher, 5000);

  const handleRebuild = async () => {
    if (!confirm("Rebuild and redeploy MultiSeat? The service will restart — dashboard will reconnect automatically.")) return;
    setRebuilding(true);
    setRebuildMsg(null);
    try {
      const res = await system.rebuild();
      setRebuildMsg(res.message + " Waiting for service to restart...");
      const poll = setInterval(async () => {
        try {
          await system.health();
          setRebuildMsg("Service restarted successfully.");
          setRebuilding(false);
          clearInterval(poll);
        } catch { /* still restarting */ }
      }, 2000);
    } catch (e) {
      setRebuildMsg(e instanceof Error ? e.message : "Rebuild failed");
      setRebuilding(false);
    }
  };

  if (loading) return <div className="text-muted">Loading system status...</div>;
  if (error) return <div className="error-banner">{error}</div>;
  if (!health) return null;

  const memUsedMb = health.systemMemoryMb - health.availableMemoryMb;
  const memPercent =
    health.systemMemoryMb > 0
      ? Math.round((memUsedMb / health.systemMemoryMb) * 100)
      : 0;

  const healthColor =
    health.health?.status === "Critical" ? "var(--danger)"
    : health.health?.status === "Warning" ? "var(--warning)"
    : "var(--success)";

  return (
    <div>
      <div className="page-header">
        <h2>System Health</h2>
        <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
          <span className="text-muted">
            Updated {new Date(health.timestamp).toLocaleTimeString()}
          </span>
          <button
            className="btn-danger"
            onClick={handleRebuild}
            disabled={rebuilding}
            title="Rebuild and redeploy MultiSeat (requires SourceDir in appsettings.json)"
          >
            {rebuilding ? "Rebuilding..." : "Rebuild & Redeploy"}
          </button>
        </div>
      </div>

      {rebuildMsg && (
        <div className={rebuildMsg.includes("failed") ? "error-banner" : "info-banner"} style={{ marginBottom: 16 }}>
          {rebuildMsg}
        </div>
      )}

      {/* Health score banner */}
      {health.health && (
        <div className="card" style={{ marginBottom: 16, borderLeft: `4px solid ${healthColor}` }}>
          <div className="card-body" style={{ display: "flex", alignItems: "center", gap: 20 }}>
            <div>
              <span style={{ fontSize: 28, fontWeight: 700, color: healthColor }}>
                {health.health.score}
              </span>
              <span className="text-muted" style={{ marginLeft: 6 }}>/ 100</span>
            </div>
            <div>
              <div style={{ fontWeight: 600, color: healthColor }}>{health.health.status}</div>
              {health.health.issues.length > 0 && (
                <ul style={{ margin: "4px 0 0", paddingLeft: 18, fontSize: 13 }}>
                  {health.health.issues.map((issue, i) => (
                    <li key={i} style={{ color: "var(--text-secondary)" }}>{issue}</li>
                  ))}
                </ul>
              )}
              {health.health.issues.length === 0 && (
                <div className="text-muted" style={{ fontSize: 13 }}>All systems nominal</div>
              )}
            </div>
          </div>
        </div>
      )}

      <div className="card-grid">
        {/* Seats */}
        <div className="card">
          <div className="card-body">
            <h3>Seats</h3>
            <div className="big-number">{health.activeSeats}</div>
            <div className="text-muted">of {health.maxSeats} max</div>
            <ProgressBar value={health.activeSeats} max={health.maxSeats} color="#3b82f6" />
          </div>
        </div>

        {/* CPU */}
        {health.cpu && (
          <div className="card">
            <div className="card-body">
              <h3>CPU</h3>
              <div style={{ fontSize: 13, color: "var(--text-secondary)", marginBottom: 8 }}>
                {health.cpu.name || "—"}
              </div>
              <div className="big-number">{health.cpu.utilizationPercent}%</div>
              <div className="text-muted">{health.cpu.logicalCores} logical cores</div>
              <ProgressBar
                value={health.cpu.utilizationPercent}
                max={100}
                color={health.cpu.utilizationPercent >= 90 ? "var(--danger)" : health.cpu.utilizationPercent >= 75 ? "var(--warning)" : "#6366f1"}
              />
            </div>
          </div>
        )}

        {/* Memory */}
        <div className="card">
          <div className="card-body">
            <h3>Memory</h3>
            <div className="big-number">{memPercent}%</div>
            <div className="text-muted">
              {formatMb(memUsedMb)} / {formatMb(health.systemMemoryMb)}
            </div>
            <ProgressBar
              value={memPercent}
              max={100}
              color={memPercent >= 90 ? "var(--danger)" : memPercent >= 80 ? "var(--warning)" : "#f59e0b"}
            />
          </div>
        </div>

        {/* GPU */}
        {health.gpu && (
          <div className="card">
            <div className="card-body">
              <h3>GPU</h3>
              <div style={{ fontSize: 14, fontWeight: 600 }}>{health.gpu.name}</div>
              <div className="stat-grid" style={{ marginTop: 12 }}>
                <div className="stat-item">
                  <span className="stat-label">Utilization</span>
                  <span className="stat-value">{health.gpu.utilizationPercent}%</span>
                </div>
                <div className="stat-item">
                  <span className="stat-label">Encoder</span>
                  <span className="stat-value">{health.gpu.encoderUtilizationPercent}%</span>
                </div>
                <div className="stat-item">
                  <span className="stat-label">VRAM</span>
                  <span className="stat-value">
                    {formatMb(health.gpu.vramUsedMb)} / {formatMb(health.gpu.vramTotalMb)}
                  </span>
                </div>
                <div className="stat-item">
                  <span className="stat-label">Encoder Sessions</span>
                  <span className="stat-value">{health.gpu.activeEncoderSessions}</span>
                </div>
              </div>
              <ProgressBar value={health.gpu.utilizationPercent} max={100} color="#10b981" />
            </div>
          </div>
        )}

        {/* Network */}
        {health.network && (
          <div className="card">
            <div className="card-body">
              <h3>Network</h3>
              <div style={{ fontSize: 13, color: "var(--text-secondary)", marginBottom: 8 }}>
                {health.network.primaryAdapter || "—"}
              </div>
              <div className="stat-grid">
                <div className="stat-item">
                  <span className="stat-label">Download</span>
                  <span className="stat-value">{formatBps(health.network.bytesReceivedPerSec)}</span>
                </div>
                <div className="stat-item">
                  <span className="stat-label">Upload</span>
                  <span className="stat-value">{formatBps(health.network.bytesSentPerSec)}</span>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* System Info */}
        <div className="card">
          <div className="card-body">
            <h3>System</h3>
            <div className="stat-grid">
              <div className="stat-item">
                <span className="stat-label">Windows</span>
                <span className="stat-value">{health.windowsBuild}</span>
              </div>
              <div className="stat-item">
                <span className="stat-label">RDP Wrapper</span>
                <span className="stat-value">
                  {health.rdpWrapperActive
                    ? <span style={{ color: "var(--success)" }}>Active</span>
                    : <span style={{ color: "var(--danger)" }}>Inactive</span>}
                </span>
              </div>
              <div className="stat-item">
                <span className="stat-label">Input Hooks</span>
                <span className="stat-value">
                  {hookStatus?.installed
                    ? <span style={{ color: "var(--success)" }}>Active</span>
                    : <span style={{ color: "var(--text-secondary)" }}>Idle</span>}
                </span>
              </div>
              <div className="stat-item">
                <span className="stat-label">API Port</span>
                <span className="stat-value">{window.location.port || "80"}</span>
              </div>
            </div>
          </div>
        </div>

        {/* API Auth */}
        {authEnabled !== null && (
          <div className="card" style={{ borderLeft: authEnabled ? undefined : "4px solid var(--warning)" }}>
            <div className="card-body">
              <h3>API Security</h3>
              <div className="stat-grid" style={{ marginBottom: 12 }}>
                <div className="stat-item">
                  <span className="stat-label">Authentication</span>
                  <span className="stat-value">
                    {authEnabled
                      ? <span style={{ color: "var(--success)" }}>Enabled</span>
                      : <span style={{ color: "var(--warning)" }}>Disabled</span>}
                  </span>
                </div>
              </div>
              {!authEnabled && (
                <div className="text-muted" style={{ fontSize: 12, marginBottom: 10 }}>
                  The API is open to anyone on the network.
                </div>
              )}
              <button
                className={authEnabled ? "btn-danger btn-sm" : "btn-sm"}
                style={authEnabled ? undefined : { background: "var(--success-dim, #166534)", borderColor: "#16a34a", color: "#4ade80" }}
                onClick={handleToggleAuth}
                disabled={authToggling}
              >
                {authToggling ? "Updating..." : authEnabled ? "Disable Auth" : "Enable Auth"}
              </button>
            </div>
          </div>
        )}

        {/* Host-wide refresh rate (issue #74) */}
        <RefreshRateControl />
      </div>

      {/* Disk usage */}
      {health.disks && health.disks.length > 0 && (
        <div className="card" style={{ marginTop: 16 }}>
          <div className="card-body">
            <h3>Disk</h3>
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              {health.disks.map((disk) => (
                <DiskRow key={disk.name} disk={disk} />
              ))}
            </div>
          </div>
        </div>
      )}

      {/* Top processes */}
      {health.topProcesses && health.topProcesses.length > 0 && (
        <div className="card" style={{ marginTop: 16 }}>
          <div className="card-body">
            <h3>Top Processes</h3>
            <table className="process-table">
              <thead>
                <tr>
                  <th>Process</th>
                  <th>PID</th>
                  <th>CPU</th>
                  <th>Memory</th>
                  <th>Connections</th>
                </tr>
              </thead>
              <tbody>
                {health.topProcesses.map((proc) => (
                  <ProcessRow key={proc.pid} proc={proc} />
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}

function DiskRow({ disk }: { disk: DiskInfo }) {
  const usedMb = disk.totalMb - disk.freeMb;
  const color = disk.usedPercent >= 90 ? "var(--danger)" : disk.usedPercent >= 75 ? "var(--warning)" : "#6366f1";
  return (
    <div>
      <div style={{ display: "flex", justifyContent: "space-between", fontSize: 13, marginBottom: 4 }}>
        <span style={{ fontWeight: 600 }}>
          {disk.name}{disk.label ? ` — ${disk.label}` : ""}
        </span>
        <span className="text-muted">
          {formatMb(usedMb)} / {formatMb(disk.totalMb)} ({disk.usedPercent}%)
        </span>
      </div>
      <ProgressBar value={disk.usedPercent} max={100} color={color} />
    </div>
  );
}

function ProcessRow({ proc }: { proc: TopProcess }) {
  return (
    <tr>
      <td style={{ fontWeight: 600 }}>{proc.name}</td>
      <td className="text-muted">{proc.pid}</td>
      <td style={{ color: proc.cpuPercent >= 50 ? "var(--warning)" : undefined }}>
        {proc.cpuPercent.toFixed(1)}%
      </td>
      <td>{formatMb(proc.memoryMb)}</td>
      <td>{proc.networkConnections > 0 ? proc.networkConnections : "—"}</td>
    </tr>
  );
}

function ProgressBar({ value, max, color }: { value: number; max: number; color: string }) {
  const pct = max > 0 ? Math.min(100, (value / max) * 100) : 0;
  return (
    <div className="progress-bar">
      <div className="progress-fill" style={{ width: `${pct}%`, backgroundColor: color }} />
    </div>
  );
}

function formatMb(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb} MB`;
}

function formatBps(bps: number): string {
  if (bps >= 1_000_000) return `${(bps / 1_000_000).toFixed(1)} MB/s`;
  if (bps >= 1_000) return `${(bps / 1_000).toFixed(0)} KB/s`;
  return `${bps} B/s`;
}
