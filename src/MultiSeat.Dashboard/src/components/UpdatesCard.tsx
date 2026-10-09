import { useEffect, useRef, useState } from "react";
import { useLocation } from "react-router-dom";
import type { UpdateComponent } from "../api/types";
import { useUpdatesContext } from "../hooks/UpdatesContext";
import { ReleaseLink, UpdateInstructions } from "./UpdateInstructions";
import { formatAge, statusLabel } from "./updateUtils";

/** Mirrors the server-side cooldown for a manual check. */
export const CHECK_COOLDOWN_MS = 60_000;

/** What leaves the machine when update checks are on (design sections 4.7 and 5). */
export const WHAT_IS_SENT =
  "Sends: GET api.github.com, generic User-Agent, nothing else. Never downloads or installs anything.";

function installedText(c: UpdateComponent): string {
  if (c.status === "notInstalled") return "not found on this host";
  if (c.status === "unknownInstalled") return "not identified";
  if (c.status === "latestOnly") return "runs on other devices, cannot be seen";
  if (c.installed) return c.installed.display || c.installed.version;
  return "unknown";
}

function latestText(c: UpdateComponent): string {
  return c.latest ? c.latest.version : "none yet";
}

function noteText(c: UpdateComponent): string | null {
  return c.installedNote || c.installed?.note || null;
}

function UpdateRow({ c }: { c: UpdateComponent }) {
  const note = noteText(c);
  return (
    <div className="update-row" data-component={c.id} data-status={c.status}>
      <div className="update-row-name">{c.name}</div>
      <div className="update-row-main">
        <span className="update-row-installed">installed: {installedText(c)}</span>
        <span className="update-row-latest">latest: {latestText(c)}</span>
        <span className={`update-row-status update-status--${c.status}`}>{statusLabel(c.status)}</span>
      </div>
      {note && <div className="update-row-note text-muted">{note}</div>}
      {c.latest && (
        <div className="update-row-link">
          <ReleaseLink url={c.latest.releaseUrl}>Release notes</ReleaseLink>
        </div>
      )}
    </div>
  );
}

export function UpdatesCard() {
  const updates = useUpdatesContext();
  const [busy, setBusy] = useState(false);
  const [checking, setChecking] = useState(false);
  const checkInFlight = useRef(false);
  const mounted = useRef(true);
  const location = useLocation();
  const [cooling, setCooling] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      clearTimeout(timer.current);
    };
  }, []);

  // Arriving at /system#updates (first render or from the "See what is sent" link) lands on this
  // card: scroll it into view and move focus to its heading for keyboard users. The card only
  // exists once the data has loaded, hence the dependency on it. A missing element is ignored.
  const hasData = updates?.data != null;
  useEffect(() => {
    if (location.hash !== "#updates" || !hasData) return;
    const card = document.getElementById("updates");
    if (!card) return;
    card.scrollIntoView?.({ block: "start" });
    card.querySelector<HTMLElement>("h3")?.focus();
  }, [location.hash, location.key, hasData]);

  const startCooldown = () => {
    setCooling(true);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setCooling(false), CHECK_COOLDOWN_MS);
  };

  if (!updates) return null;
  const data = updates.data;

  if (!data) {
    return (
      <div className="card update-card" id="updates">
        <div className="card-body">
          <h3 tabIndex={-1}>Updates</h3>
          <div className="text-muted" style={{ fontSize: 13 }}>
            {updates.loaded ? "Update information is not available right now." : "Loading..."}
          </div>
        </div>
      </div>
    );
  }

  // The service runs the check inside this request, which can take about 30 seconds. The button
  // stays disabled for the whole time and a second click cannot start a second request.
  const handleCheck = async () => {
    if (checkInFlight.current) return;
    checkInFlight.current = true;
    setChecking(true);
    setMessage(null);
    const r = await updates.checkNow();
    checkInFlight.current = false;
    if (!mounted.current) return;
    setChecking(false);
    if (r.ok) {
      startCooldown();
    } else {
      setMessage(r.message);
      if (r.kind === "cooldown") startCooldown();
    }
  };

  const handleEnabled = async (enabled: boolean) => {
    setBusy(true);
    setMessage(null);
    const r = await updates.setEnabled(enabled);
    if (!mounted.current) return;
    setBusy(false);
    if (!r.ok) setMessage(r.message);
  };

  // ── Off: nothing has contacted the internet; offer the one-click opt-in ──
  if (!data.enabled) {
    return (
      <div className="card update-card" id="updates">
        <div className="card-body">
          <h3 tabIndex={-1}>Updates</h3>
          <p className="update-off-lead">
            Update checks are off. MultiSeat has not contacted the internet.
          </p>
          <p className="text-muted update-sent">
            If you turn them on, about every {data.intervalHours} hours this host asks api.github.com for the
            release lists of vibesoftwarecoder/MultiSeat, vibesoftwarecoder/ApolloVibe and
            vibesoftwarecoder/MoonlightVibe. {WHAT_IS_SENT} The request carries a generic User-Agent and no
            version, host name, user name, seat data or API key. GitHub, and anyone who can see your network
            traffic, can see that this host's IP address asked for those pages. Details are in
            docs/security-posture.md, "Outbound connections".
          </p>
          <button className="btn-sm" onClick={() => handleEnabled(true)} disabled={busy}>
            {busy ? "Updating..." : "Turn on update checks"}
          </button>
          {message && <div className="text-muted update-message" role="status">{message}</div>}
          {data.components.length > 0 && (
            <div className="update-rows">
              {data.components.map((c) => (
                <UpdateRow key={c.id} c={c} />
              ))}
            </div>
          )}
        </div>
      </div>
    );
  }

  // ── On ──
  const checkDisabled = busy || checking || cooling;
  return (
    <div className="card update-card" id="updates">
      <div className="card-body">
        <div className="update-card-head">
          <h3 tabIndex={-1}>Updates</h3>
          <span className="text-muted update-checked">Last checked {formatAge(data.checkedAt)}</span>
          <button className="btn-sm" onClick={handleCheck} disabled={checkDisabled}>
            {checking ? "Checking…" : "Check now"}
          </button>
        </div>
        {checking && (
          <div className="text-muted update-message" role="status">
            Checking GitHub for new releases. This can take up to 30 seconds.
          </div>
        )}
        {cooling && !checking && !message && (
          <div className="text-muted update-message" role="status">
            A check just ran. You can check again in a minute.
          </div>
        )}
        {message && <div className="text-muted update-message" role="status">{message}</div>}
        {data.error && <div className="text-muted update-message">Last check failed: {data.error}</div>}

        <div className="update-rows">
          {data.components.map((c) => (
            <UpdateRow key={c.id} c={c} />
          ))}
        </div>

        <div className="update-toggle">
          <span>Update checks: ON</span>
          <button className="btn-sm" onClick={() => handleEnabled(false)} disabled={busy || checking}>
            Turn off
          </button>
        </div>
        <div className="text-muted update-sent">{WHAT_IS_SENT}</div>

        <h4 className="update-howto-title">How to update</h4>
        <div className="text-muted update-howto-lead">
          Nothing here runs anything. These are steps for you to follow on the host.
        </div>
        {data.components.map((c) => (
          <details key={c.id} className="update-howto" data-component={c.id}>
            <summary>{c.name}</summary>
            <UpdateInstructions component={c} />
          </details>
        ))}
      </div>
    </div>
  );
}
