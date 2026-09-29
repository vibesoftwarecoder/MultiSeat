import { useState, useEffect, useCallback } from "react";
import type { SeatInfo, SeatServices, NvencQualityPreset, ScaleFactorSource } from "../api/types";
import { seats as seatsApi } from "../api/client";
import { StatusBadge } from "./StatusBadge";
import { ResizeControl } from "./ResizeControl";

const PROVISION_STEPS: { key: string; label: string }[] = [
  { key: "Session",       label: "Session"    },
  { key: "Display",       label: "Display"    },
  { key: "Audio",         label: "Audio"      },
  { key: "Apollo",        label: "Apollo"     },
  { key: "DetectDisplay", label: "Display ID" },
];

const STATUS_CARD_CLASS: Partial<Record<string, string>> = {
  Streaming:    "card--streaming",
  Ready:        "card--ready",
  Provisioning: "card--provisioning",
  Configuring:  "card--provisioning",
  Error:        "card--error",
};

interface Props {
  seat: SeatInfo;
  onUpdate: () => void;
}

export function SeatCard({ seat, onUpdate }: Props) {
  const [launching, setLaunching] = useState(false);
  const [destroying, setDestroying] = useState(false);
  const [confirmDestroy, setConfirmDestroy] = useState(false);
  const [launchPath, setLaunchPath] = useState("");
  const [showLaunch, setShowLaunch] = useState(false);
  const [services, setServices] = useState<SeatServices | null>(null);
  const [showServices, setShowServices] = useState(false);
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const [autoStartLoading, setAutoStartLoading] = useState(false);
  const [presetLoading, setPresetLoading] = useState(false);
  const [clients, setClients] = useState<string[] | null>(null);
  const [unpairingClient, setUnpairingClient] = useState<string | null>(null);

  const isActive = seat.status === "Ready" || seat.status === "Streaming";
  const isProvisioning = seat.status === "Provisioning" || seat.status === "Configuring";
  const accentClass = STATUS_CARD_CLASS[seat.status] ?? "";

  const fetchServices = useCallback(async () => {
    if (!showServices || seat.status === "Idle") return;
    try {
      const [s, c] = await Promise.all([
        seatsApi.services(seat.id),
        seatsApi.clients(seat.id),
      ]);
      setServices(s);
      setClients(c);
    } catch { /* ignore */ }
  }, [seat.id, seat.status, showServices]);

  useEffect(() => {
    fetchServices();
  }, [fetchServices, showServices]);

  // Auto-cancel teardown confirm after 4 seconds
  useEffect(() => {
    if (!confirmDestroy) return;
    const t = setTimeout(() => setConfirmDestroy(false), 4000);
    return () => clearTimeout(t);
  }, [confirmDestroy]);

  const handleNvencPreset = async (preset: NvencQualityPreset) => {
    if (preset === seat.nvencPreset || presetLoading) return;
    setPresetLoading(true);
    try {
      await seatsApi.setNvencPreset(seat.id, preset);
      onUpdate();
    } catch (e) {
      alert(e instanceof Error ? e.message : "Failed to change quality preset");
    } finally {
      setPresetLoading(false);
    }
  };

  const handleAutoStart = async () => {
    setAutoStartLoading(true);
    try {
      await seatsApi.setAutoStart(seat.id, !seat.autoStart);
      onUpdate();
    } catch (e) {
      alert(e instanceof Error ? e.message : "Failed to update auto-start");
    } finally {
      setAutoStartLoading(false);
    }
  };

  const handleDestroy = async () => {
    setConfirmDestroy(false);
    setDestroying(true);
    try {
      await seatsApi.destroy(seat.id);
      onUpdate();
    } catch (e) {
      alert(e instanceof Error ? e.message : "Failed to destroy seat");
    } finally {
      setDestroying(false);
    }
  };

  const handleLaunch = async () => {
    if (!launchPath.trim()) return;
    setLaunching(true);
    try {
      await seatsApi.launch(seat.id, { executablePath: launchPath.trim() });
      setShowLaunch(false);
      setLaunchPath("");
      onUpdate();
    } catch (e) {
      alert(e instanceof Error ? e.message : "Failed to launch app");
    } finally {
      setLaunching(false);
    }
  };

  const handleUnpair = async (name: string) => {
    setUnpairingClient(name);
    try {
      await seatsApi.unpairClient(seat.id, name);
      setClients((prev) => prev?.filter((c) => c !== name) ?? null);
    } catch (e) {
      alert(e instanceof Error ? e.message : "Failed to unpair client");
    } finally {
      setUnpairingClient(null);
    }
  };

  const handleUnpairAll = async () => {
    setUnpairingClient("all");
    try {
      await seatsApi.unpairAll(seat.id);
      setClients([]);
    } catch (e) {
      alert(e instanceof Error ? e.message : "Failed to unpair clients");
    } finally {
      setUnpairingClient(null);
    }
  };

  const serviceAction = async (name: string, fn: () => Promise<unknown>) => {
    setActionLoading(name);
    try {
      await fn();
      onUpdate();
      fetchServices();
    } catch (e) {
      alert(e instanceof Error ? e.message : `Failed: ${name}`);
    } finally {
      setActionLoading(null);
    }
  };

  const moonlightPort = seat.portBase > 0 ? seat.portBase : null;
  const uptime = seat.readyAt ? formatDuration(new Date(seat.readyAt)) : null;
  const showControls = seat.status !== "Idle" && seat.status !== "TearingDown";

  return (
    <div className={`card ${accentClass}`}>
      <div className="card-header">
        <div>
          <h3 style={{ margin: 0 }}>{seat.accountName}</h3>
          <span className="text-muted" style={{ fontSize: 12 }}>
            {seat.id.substring(0, 8)}
          </span>
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          {seat.status === "Streaming" && <span className="live-badge">● CONNECTED</span>}
          <StatusBadge status={seat.status} />
        </div>
      </div>

      <div className="card-body">
        {/* Provisioning step indicator */}
        {isProvisioning && (
          <ProvisioningSteps currentStep={seat.provisioningStep} />
        )}

        <div className="stat-grid">
          <StatItem
            label="Session"
            value={seat.sessionId >= 0 ? `#${seat.sessionId}` : "--"}
          />
          <StatItem
            label="Resolution"
            value={`${seat.width}x${seat.height}@${seat.fps}`}
          />
          <StatItem
            label="Port"
            value={moonlightPort ? String(moonlightPort) : "--"}
          />
          <StatItem
            label="Apollo PID"
            value={seat.apolloProcessId > 0 ? String(seat.apolloProcessId) : "--"}
          />
        </div>

        {/* Set at provision when the seat's fps is above what the host composes (issue #70). */}
        {seat.effectiveRefreshRateWarning && (
          <div
            className="text-muted"
            style={{ fontSize: 12, marginTop: 8, color: "var(--warning)" }}
            title="Change the host's refresh rate on the System page. It applies to a seat's next session."
          >
            {seat.effectiveRefreshRateWarning}
          </div>
        )}

        {moonlightPort && isActive && (
          <MoonlightAddress host={window.location.hostname} port={moonlightPort} />
        )}

        {seat.scaleFactor > 0 && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 8 }}>
            Scale: {seat.scaleFactor}% ({scaleSourceLabel(seat.scaleFactorSource)})
          </div>
        )}

        {seat.launchApp && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 8 }}>
            App: {seat.launchApp}
          </div>
        )}

        {seat.errorMessage && (
          <div className="error-banner">{seat.errorMessage}</div>
        )}

        {uptime && (
          <div className="text-muted" style={{ fontSize: 12, marginTop: 4 }}>
            Uptime: {uptime}
          </div>
        )}

        {/* Controls row: auto-start + quality preset side by side */}
        {showControls && (
          <div className="controls-row">
            <div className="control-group">
              <span className="stat-label">Auto-start</span>
              <button
                className={`toggle-btn${seat.autoStart ? " toggle-btn--on" : ""}`}
                onClick={handleAutoStart}
                disabled={autoStartLoading}
                title={seat.autoStart ? "Disable auto-start" : "Enable auto-start"}
              >
                {autoStartLoading ? "..." : seat.autoStart ? "On" : "Off"}
              </button>
            </div>
            <div className="control-group">
              <span className="stat-label">Quality</span>
              <div className="preset-toggle">
                {(["Latency", "Balanced", "Quality"] as NvencQualityPreset[]).map((p) => (
                  <button
                    key={p}
                    className={`preset-btn${seat.nvencPreset === p ? " preset-btn--active" : ""}`}
                    onClick={() => handleNvencPreset(p)}
                    disabled={presetLoading}
                    title={
                      p === "Latency" ? "P1 — lowest encode latency"
                      : p === "Quality" ? "P7 — best quality, more GPU"
                      : "P4 — balanced (default)"
                    }
                  >
                    {p}
                  </button>
                ))}
              </div>
            </div>
          </div>
        )}

        {/* Live resize: only once the seat has a working session to reconnect (issue #70) */}
        {isActive && <ResizeControl seat={seat} onUpdate={onUpdate} />}

        {/* Service Management Panel */}
        {showControls && (
          <div style={{ marginTop: 12 }}>
            <button
              className="btn-ghost btn-sm"
              onClick={() => setShowServices(!showServices)}
              style={{ width: "100%", textAlign: "left" }}
            >
              {showServices ? "▾" : "▸"} Manage Services
            </button>

            {showServices && services && (
              <div className="service-panel">
                {/* Paired Moonlight clients */}
                <div className="paired-clients">
                  <div className="paired-clients-header">
                    <span className="stat-label">Paired Clients</span>
                    {clients && clients.length > 1 && (
                      <button
                        className="btn-sm btn-danger"
                        disabled={unpairingClient !== null}
                        onClick={handleUnpairAll}
                        title="Remove all paired Moonlight clients"
                      >
                        {unpairingClient === "all" ? "..." : "Unpair All"}
                      </button>
                    )}
                  </div>
                  {!clients || clients.length === 0 ? (
                    <span className="text-muted" style={{ fontSize: 12 }}>No paired clients</span>
                  ) : (
                    clients.map((name) => (
                      <div key={name} className="paired-client-row">
                        <span style={{ fontSize: 13 }}>{name}</span>
                        <button
                          className="btn-sm btn-danger"
                          disabled={unpairingClient !== null}
                          onClick={() => handleUnpair(name)}
                          title={`Unpair ${name}`}
                        >
                          {unpairingClient === name ? "..." : "Unpair"}
                        </button>
                      </div>
                    ))
                  )}
                </div>
                <ServiceRow
                  name="Apollo"
                  active={services.apollo}
                  detail={services.apolloRestarts > 0 ? `${services.apolloRestarts} restarts` : undefined}
                  actions={
                    <>
                      {seat.portBase > 0 && (
                        <a
                          href={`https://${window.location.hostname}:${seat.portBase + 1}`}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="btn-sm btn-link"
                          title="Open Apollo Web UI"
                        >
                          Open
                        </a>
                      )}
                      {services.apollo ? (
                        <>
                          <button
                            className="btn-sm"
                            disabled={actionLoading !== null}
                            onClick={() => serviceAction("apollo-restart", () => seatsApi.apolloRestart(seat.id))}
                          >
                            {actionLoading === "apollo-restart" ? "..." : "Restart"}
                          </button>
                          <button
                            className="btn-sm btn-danger"
                            disabled={actionLoading !== null}
                            onClick={() => serviceAction("apollo-stop", () => seatsApi.apolloStop(seat.id))}
                          >
                            {actionLoading === "apollo-stop" ? "..." : "Stop"}
                          </button>
                        </>
                      ) : (
                        <button
                          className="btn-sm"
                          disabled={actionLoading !== null}
                          onClick={() => serviceAction("apollo-start", () => seatsApi.apolloStart(seat.id))}
                        >
                          {actionLoading === "apollo-start" ? "..." : "Start"}
                        </button>
                      )}
                    </>
                  }
                />
                {/* Separate from the process row on purpose: a live process that is not
                    answering is the failure a user actually notices, and it would look
                    healthy if reachability were folded into the Apollo light. */}
                <ServiceRow
                  name="Apollo API"
                  active={services.apolloReachable}
                  detail={
                    services.apolloReachable
                      ? services.apolloStreaming
                        ? "streaming"
                        : `answering on ${seat.portBase}`
                      : services.apollo
                        ? "process up, not answering"
                        : "not running"
                  }
                />
                <ServiceRow
                  name="Display"
                  active={services.display}
                  actions={
                    <button
                      className="btn-sm"
                      disabled={actionLoading !== null}
                      onClick={() => serviceAction("display-reset", () => seatsApi.resetDisplay(seat.id))}
                    >
                      {actionLoading === "display-reset" ? "..." : "Reset"}
                    </button>
                  }
                />
                {services.audioManaged ? (
                  <ServiceRow
                    name="Audio"
                    active={services.audio}
                    detail={seat.vacCableIndex >= 0 ? `VAC #${seat.vacCableIndex}` : undefined}
                    actions={
                      <button
                        className="btn-sm"
                        disabled={actionLoading !== null}
                        onClick={() => serviceAction("audio-reset", () => seatsApi.resetAudio(seat.id))}
                      >
                        {actionLoading === "audio-reset" ? "..." : "Reset"}
                      </button>
                    }
                  />
                ) : (
                  // Per-session audio: no MultiSeat-assigned cable. The seat's RDP session owns
                  // a private "Remote Audio" endpoint that Apollo captures directly, so there is
                  // no device that could be missing and nothing to reset.
                  <ServiceRow
                    name="Audio"
                    active
                    detail="Session — Apollo captures the seat's own endpoint"
                  />
                )}
                {services.controllerManaged ? (
                  <ServiceRow
                    name="Controller"
                    active={services.controller}
                    detail={seat.viGEmControllerIndex >= 0 ? `ViGEm #${seat.viGEmControllerIndex}` : undefined}
                    actions={
                      <button
                        className="btn-sm"
                        disabled={actionLoading !== null}
                        onClick={() => serviceAction("controller-reset", () => seatsApi.resetController(seat.id))}
                      >
                        {actionLoading === "controller-reset" ? "..." : "Reset"}
                      </button>
                    }
                  />
                ) : (
                  // Default mode: no MultiSeat-managed pad — Apollo forwards the Moonlight
                  // client's controller natively. Show it as working ("Native"), not a
                  // down/grey light, and offer no Reset (there's nothing to reset).
                  <ServiceRow
                    name="Controller"
                    active
                    detail="Native — Apollo forwards client input"
                  />
                )}
                <ServiceRow
                  name="Session"
                  active={seat.sessionId > 0}
                  detail={seat.sessionId > 0 ? `ID: ${seat.sessionId}` : "no session"}
                  actions={
                    <button
                      className="btn-sm"
                      disabled={actionLoading !== null}
                      onClick={() => serviceAction("session-reconnect", () => seatsApi.sessionReconnect(seat.id))}
                      title="Reconnect mstsc to keep session Active."
                    >
                      {actionLoading === "session-reconnect" ? "..." : "Reconnect"}
                    </button>
                  }
                />
                <ServiceRow name="Firewall" active={services.firewall} />
                <ServiceRow name="Input Hooks" active={services.inputHooks} />
              </div>
            )}
          </div>
        )}
      </div>

      <div className="card-actions">
        {/* Launch app — hidden while confirming teardown */}
        {isActive && !confirmDestroy && (
          showLaunch ? (
            <div style={{ display: "flex", gap: 4, flex: 1 }}>
              <input
                type="text"
                placeholder="C:\path\to\game.exe"
                value={launchPath}
                onChange={(e) => setLaunchPath(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && handleLaunch()}
                style={{ flex: 1 }}
                disabled={launching}
              />
              <button onClick={handleLaunch} disabled={launching || !launchPath.trim()}>
                {launching ? "..." : "Go"}
              </button>
              <button className="btn-ghost" onClick={() => setShowLaunch(false)}>
                X
              </button>
            </div>
          ) : (
            <button onClick={() => setShowLaunch(true)}>Launch App</button>
          )
        )}

        {/* Teardown — two-step inline confirmation */}
        {showControls && (
          confirmDestroy ? (
            <div className="teardown-confirm">
              <span className="text-muted" style={{ fontSize: 12 }}>Confirm teardown?</span>
              <button className="btn-danger btn-sm" onClick={handleDestroy} disabled={destroying}>
                {destroying ? "..." : "Yes, teardown"}
              </button>
              <button className="btn-ghost btn-sm" onClick={() => setConfirmDestroy(false)}>
                Cancel
              </button>
            </div>
          ) : (
            <button
              className="btn-danger"
              onClick={() => setConfirmDestroy(true)}
              disabled={destroying}
              style={{ marginLeft: "auto" }}
            >
              Teardown
            </button>
          )
        )}
      </div>
    </div>
  );
}

function ProvisioningSteps({ currentStep }: { currentStep: string | null }) {
  const currentIdx = PROVISION_STEPS.findIndex((s) => s.key === currentStep);

  return (
    <div className="provision-steps">
      {PROVISION_STEPS.map((step, idx) => {
        const isDone = idx < currentIdx;
        const isCurrent = idx === currentIdx;
        return (
          <div
            key={step.key}
            className={`provision-step${isDone ? " done" : ""}${isCurrent ? " current" : ""}`}
          >
            <div className="provision-dot">
              {isDone ? "✓" : null}
            </div>
            <span className="provision-label">{step.label}</span>
          </div>
        );
      })}
    </div>
  );
}

function ServiceRow({
  name,
  active,
  detail,
  actions,
}: {
  name: string;
  active: boolean;
  detail?: string;
  actions?: React.ReactNode;
}) {
  return (
    <div className="service-row">
      <div className="service-status">
        <span
          className="service-dot"
          style={{ background: active ? "var(--success)" : "var(--text-secondary)" }}
        />
        <span className="service-name">{name}</span>
        {detail && (
          <span className="text-muted" style={{ fontSize: 11 }}>
            ({detail})
          </span>
        )}
      </div>
      {actions && <div className="service-actions">{actions}</div>}
    </div>
  );
}

function MoonlightAddress({ host, port }: { host: string; port: number }) {
  const [copied, setCopied] = useState(false);
  const address = `${host}:${port}`;

  const handleCopy = async () => {
    await navigator.clipboard.writeText(address);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="moonlight-info">
      <span className="stat-label">Moonlight</span>
      <code style={{ flex: 1 }}>{address}</code>
      <button className="copy-btn" onClick={handleCopy} title="Copy address">
        {copied ? "Copied!" : "Copy"}
      </button>
    </div>
  );
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

function scaleSourceLabel(source: ScaleFactorSource): string {
  switch (source) {
    case "Seat":
      return "seat override";
    case "HostDefault":
      return "host default";
    default:
      return "from width";
  }
}
