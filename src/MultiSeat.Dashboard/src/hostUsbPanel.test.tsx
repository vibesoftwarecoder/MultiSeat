import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { UsbState, UsbStatus } from "./api/types";
import { HostUsbPanel } from "./components/HostUsbPanel";

function status(over: Partial<UsbStatus> = {}): UsbStatus {
  return {
    enabled: true,
    listening: true,
    port: 47996,
    requireStream: true,
    forwards: [],
    clients: [
      { uuid: "AB-1", name: "Player One", usbPermission: true },
      { uuid: "CD-2", name: "Player Two", usbPermission: false },
    ],
    denied: [],
    ...over,
  };
}

let state: UsbState;
let calls: { key: string; body: unknown }[];
let postReply: () => Response;
let getRefused: boolean;

beforeEach(() => {
  vi.useFakeTimers();
  calls = [];
  localStorage.clear();
  state = { available: true, status: status() };
  postReply = () => new Response(JSON.stringify({}), { status: 200 });
  getRefused = false;
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string, init?: RequestInit) => {
      const key = `${init?.method ?? "GET"} ${url}`;
      calls.push({ key, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (key === "GET /api/host/usb") {
        if (getRefused) {
          return Promise.resolve(
            new Response(JSON.stringify({ error: "console_session_required", reason: "NotConsoleSession" }), { status: 403 })
          );
        }
        return Promise.resolve(new Response(JSON.stringify(state), { status: 200 }));
      }
      return Promise.resolve(postReply());
    })
  );
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const flush = () =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });

async function show() {
  render(<HostUsbPanel />);
  await flush();
}

const posts = () => calls.filter((c) => c.key.startsWith("POST"));

describe("HostUsbPanel", () => {
  it("says how to switch the control pipe on when Apollo has none", async () => {
    state = { available: false, reason: "pipe_unavailable" };
    await show();

    expect(screen.getByText(/USB Forwarding Control Pipe/)).toBeTruthy();
    expect(screen.queryByText("Turn on")).toBeNull();
    expect(screen.queryByText("Turn off")).toBeNull();
  });

  it("names the failure instead of showing a code", async () => {
    state = { available: false, reason: "untrusted_server" };
    await show();
    expect(screen.getByText(/not served by the Apollo that streams on this host/)).toBeTruthy();
  });

  it("shows the tunnel, the forwarded devices and the paired clients", async () => {
    state = {
      available: true,
      status: status({
        forwards: [{ clientUuid: "AB-1", clientName: "Player One", busid: "1-2", sinceUnix: 1700000000 }],
      }),
    };
    await show();

    expect(screen.getByText(/On, listening on port 47996/)).toBeTruthy();
    expect(screen.getByText(/device 1-2/)).toBeTruthy();
    expect(screen.getByText("Player One")).toBeTruthy();
    expect(screen.getByText("May forward USB")).toBeTruthy();
    expect(screen.getByText("No USB permission")).toBeTruthy();
  });

  it("is honest that a switch made here is temporary", async () => {
    await show();
    expect(screen.getByText(/lasts until Apollo restarts/)).toBeTruthy();
  });

  it("turns the tunnel off with one request", async () => {
    await show();
    fireEvent.click(screen.getByText("Turn off"));
    await flush();

    expect(posts()).toEqual([{ key: "POST /api/host/usb/enabled", body: { enabled: false } }]);
  });

  it("turns it on when it is off", async () => {
    state = { available: true, status: status({ enabled: false, listening: false, port: 0 }) };
    await show();
    fireEvent.click(screen.getByText("Turn on"));
    await flush();

    expect(posts()).toEqual([{ key: "POST /api/host/usb/enabled", body: { enabled: true } }]);
  });

  it("blocks a client by its uuid", async () => {
    await show();
    fireEvent.click(screen.getAllByText("Block")[0]);
    await flush();

    expect(posts()).toEqual([{ key: "POST /api/host/usb/revoke", body: { clientUuid: "AB-1" } }]);
  });

  it("shows a blocked client as blocked and lets it be allowed again", async () => {
    state = { available: true, status: status({ denied: ["ab-1"] }) }; // spelling may differ
    await show();

    expect(screen.getByText("Blocked")).toBeTruthy();
    fireEvent.click(screen.getByText("Allow again"));
    await flush();

    expect(posts()).toEqual([{ key: "POST /api/host/usb/clear-revoke", body: { clientUuid: "AB-1" } }]);
  });

  it("lists a blocked uuid that belongs to nobody paired, so a typo can be undone", async () => {
    state = { available: true, status: status({ denied: ["EF-3"] }) };
    await show();

    expect(screen.getByText(/Blocked, but not paired/)).toBeTruthy();
    expect(screen.getByText(/EF-3/)).toBeTruthy();
    fireEvent.click(screen.getByText("Allow again"));
    await flush();
    expect(posts()).toEqual([{ key: "POST /api/host/usb/clear-revoke", body: { clientUuid: "EF-3" } }]);
  });

  it("tells the person to use the host's desktop when the service refuses (403)", async () => {
    postReply = () => new Response(JSON.stringify({ error: "console_session_required" }), { status: 403 });
    await show();
    fireEvent.click(screen.getByText("Turn off"));
    await flush();

    expect(screen.getByRole("alert").textContent).toMatch(/host's own desktop/);
  });

  it("says the card is for the host's own desktop when even reading is refused (403)", async () => {
    getRefused = true;
    await show();

    expect(screen.getByText(/only be seen and changed from the host's own desktop/)).toBeTruthy();
    expect(screen.queryByText(/console_session_required/)).toBeNull();
    expect(screen.queryByText("Turn on")).toBeNull();
    expect(screen.queryByText("Turn off")).toBeNull();
    expect(screen.queryByText("Player One")).toBeNull();
  });

  it("explains a refused port in words", async () => {
    postReply = () => new Response(JSON.stringify({ error: "port_refused" }), { status: 409 });
    state = { available: true, status: status({ enabled: false, listening: false }) };
    await show();
    fireEvent.click(screen.getByText("Turn on"));
    await flush();

    expect(screen.getByRole("alert").textContent).toMatch(/port/);
  });

  it("refreshes by itself", async () => {
    await show();
    const before = calls.filter((c) => c.key === "GET /api/host/usb").length;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(calls.filter((c) => c.key === "GET /api/host/usb").length).toBeGreaterThan(before);
  });
});
