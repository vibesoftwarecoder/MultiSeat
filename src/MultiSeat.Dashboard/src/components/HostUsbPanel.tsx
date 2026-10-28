import { useCallback, useEffect, useState } from "react";
import type { UsbClient, UsbStatus } from "../api/types";
import { ApiError, hostUsb } from "../api/client";

/**
 * USB forwarding of the host's own Apollo: what is forwarded now, who may do it, and the switches.
 *
 * The service serves this panel, reading and changing alike, only when the request comes from the
 * host's own desktop: the status names paired clients and the devices they forward. From anywhere else
 * it answers 403 and this panel says so in plain words instead of showing a code.
 *
 * Nothing here edits Apollo's settings. A switch made here lasts until that Apollo restarts; what it
 * starts as is the "USB Forwarding" setting in Apollo's own Network tab.
 */

/** What a "not available" reason from the service means, for the person looking at the card. */
export function describeUnavailable(reason: string): string {
  switch (reason) {
    case "console_session_required":
      return "USB forwarding can only be seen and changed from the host's own desktop. Open this dashboard there.";
    case "pipe_unavailable":
      return "Not available yet. In Apollo's web UI, open the Network tab, tick “USB Forwarding Control Pipe”, save, and restart Apollo.";
    case "no_host_apollo":
      return "Apollo is not running on this host, so there is nothing to control.";
    case "untrusted_server":
      return "Refused. The control pipe is not served by the Apollo that streams on this host, so nothing was sent to it.";
    case "busy":
    case "timeout":
      return "Apollo did not answer in time. This panel will try again in a few seconds.";
    case "bad_reply":
      return "Apollo sent an answer this version of MultiSeat does not understand. Update MultiSeat and Apollo together.";
    default:
      return "USB forwarding cannot be controlled right now.";
  }
}

/** What a failed action means. 403 is the one people will meet: the request was not from the host's desktop. */
export function describeActionError(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 403) {
      return "USB forwarding can only be changed from the host's own desktop. Open this dashboard there and try again.";
    }
    switch (e.message) {
      case "port_refused":
        return "Apollo refused to start it: the USB forwarding port is out of range or one that Apollo already uses.";
      case "start_failed":
        return "Apollo could not start the USB tunnel. Its log says why.";
      case "deny_list_full":
        return "The list of blocked clients is full, so this client could not be added. Its running devices were still cut.";
      case "shutting_down":
        return "Apollo is shutting down.";
      default:
        return describeUnavailable(e.message);
    }
  }
  return e instanceof Error ? e.message : "The request failed.";
}

export function HostUsbPanel() {
  const [state, setState] = useState<{ available: true; status: UsbStatus } | { available: false; reason: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  const load = useCallback(async () => {
    try {
      setState(await hostUsb.get());
    } catch (e) {
      setState({ available: false, reason: e instanceof ApiError ? e.message : "bad_reply" });
    }
  }, []);

  useEffect(() => {
    load();
    const t = setInterval(load, 10000);
    return () => clearInterval(t);
  }, [load]);

  const act = async (action: () => Promise<unknown>) => {
    setPending(true);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(describeActionError(e));
    } finally {
      setPending(false);
      await load();
    }
  };

  if (!state) return null;

  return (
    <div style={{ marginTop: 16, paddingTop: 12, borderTop: "1px solid var(--border, #2a2f3a)" }}>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8 }}>
        <strong>USB forwarding</strong>
        {state.available && (
          <button
            className="btn-sm"
            disabled={pending}
            onClick={() => act(() => hostUsb.setEnabled(!state.status.enabled))}
          >
            {state.status.enabled ? "Turn off" : "Turn on"}
          </button>
        )}
      </div>

      {!state.available && (
        <div className="text-muted" style={{ fontSize: 12, marginTop: 6 }}>
          {describeUnavailable(state.reason)}
        </div>
      )}

      {error && (
        <div className="error-banner" style={{ marginTop: 8 }} role="alert">
          {error}
        </div>
      )}

      {state.available && <Details status={state.status} pending={pending} act={act} />}
    </div>
  );
}

function Details({
  status,
  pending,
  act,
}: {
  status: UsbStatus;
  pending: boolean;
  act: (action: () => Promise<unknown>) => Promise<void>;
}) {
  const deniedSet = new Set(status.denied.map((u) => u.toUpperCase()));
  const pairedSet = new Set(status.clients.map((c) => c.uuid.toUpperCase()));
  const strangers = status.denied.filter((u) => !pairedSet.has(u.toUpperCase()));

  return (
    <>
      <div className="text-muted" style={{ fontSize: 12, marginTop: 6 }}>
        {status.enabled
          ? status.listening
            ? `On, listening on port ${status.port}.`
            : "On, but not listening. Apollo's log says why."
          : "Off."}
        {status.requireStream ? " A client needs an active stream to forward a device." : ""}
      </div>
      <div className="text-muted" style={{ fontSize: 12, marginTop: 4 }}>
        A switch made here lasts until Apollo restarts. What it starts as is the “USB Forwarding” setting in
        Apollo's Network tab. Each client also needs the USB permission, which you give in Apollo's client list.
      </div>

      <h4 style={{ margin: "12px 0 4px", fontSize: 13 }}>Devices forwarded now</h4>
      {status.forwards.length === 0 ? (
        <div className="text-muted" style={{ fontSize: 12 }}>None.</div>
      ) : (
        <ul style={{ margin: 0, paddingLeft: 18, fontSize: 12 }}>
          {status.forwards.map((f) => (
            <li key={`${f.clientUuid}/${f.busid}`}>
              {f.clientName || f.clientUuid} &middot; device {f.busid} &middot; since{" "}
              {new Date(f.sinceUnix * 1000).toLocaleTimeString()}
            </li>
          ))}
        </ul>
      )}

      <h4 style={{ margin: "12px 0 4px", fontSize: 13 }}>Paired clients</h4>
      {status.clients.length === 0 ? (
        <div className="text-muted" style={{ fontSize: 12 }}>No paired clients.</div>
      ) : (
        <div className="data-table-wrap">
          <table className="data-table">
            <tbody>
              {status.clients.map((c) => (
                <ClientRow key={c.uuid} client={c} blocked={deniedSet.has(c.uuid.toUpperCase())} pending={pending} act={act} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      <div className="text-muted" style={{ fontSize: 11, marginTop: 4 }}>
        Blocking cuts the client's devices now and refuses new ones until Apollo restarts or you allow it again.
        The client stays paired and keeps its other permissions.
      </div>

      {strangers.length > 0 && (
        <>
          <h4 style={{ margin: "12px 0 4px", fontSize: 13 }}>Blocked, but not paired</h4>
          <ul style={{ margin: 0, paddingLeft: 18, fontSize: 12 }}>
            {strangers.map((u) => (
              <li key={u}>
                {u}{" "}
                <button className="btn-sm btn-ghost" disabled={pending} onClick={() => act(() => hostUsb.clearRevoke(u))}>
                  Allow again
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
    </>
  );
}

function ClientRow({
  client,
  blocked,
  pending,
  act,
}: {
  client: UsbClient;
  blocked: boolean;
  pending: boolean;
  act: (action: () => Promise<unknown>) => Promise<void>;
}) {
  return (
    <tr>
      <td>{client.name || client.uuid}</td>
      <td className="text-muted">
        {blocked ? "Blocked" : client.usbPermission ? "May forward USB" : "No USB permission"}
      </td>
      <td style={{ textAlign: "right" }}>
        {blocked ? (
          <button className="btn-sm btn-ghost" disabled={pending} onClick={() => act(() => hostUsb.clearRevoke(client.uuid))}>
            Allow again
          </button>
        ) : (
          <button className="btn-sm btn-danger" disabled={pending} onClick={() => act(() => hostUsb.revoke(client.uuid))}>
            Block
          </button>
        )}
      </td>
    </tr>
  );
}
