import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { UpdateComponent, UpdatesState } from "./api/types";
import { UpdatesProvider } from "./hooks/UpdatesContext";
import { UpdateBanner } from "./components/UpdateBanner";
import { UpdatesCard } from "./components/UpdatesCard";
import { SettingsPage } from "./pages/SettingsPage";
import App from "./App";
import { DISMISS_KEY, safeReleaseUrl, visibleAnnouncements } from "./components/updateUtils";

// ── Fixtures ────────────────────────────────────────────────────

const REL = "https://github.com/vibesoftwarecoder";

function multiseat(over: Partial<UpdateComponent> = {}): UpdateComponent {
  return {
    id: "multiseat",
    name: "MultiSeat",
    status: "updateAvailable",
    installed: { version: "0.6.18", display: "0.6.18 (ebcd253)", source: "assembly", note: null },
    latest: { version: "0.6.19", tag: "v0.6.19", publishedAt: "2026-10-09T10:20:56Z", releaseUrl: `${REL}/MultiSeat/releases/tag/v0.6.19` },
    announce: true,
    checkedAt: "2026-10-09T14:02:11Z",
    error: null,
    ...over,
  };
}

function apollo(over: Partial<UpdateComponent> = {}): UpdateComponent {
  return {
    id: "apollovibe",
    name: "ApolloVibe",
    status: "unknownInstalled",
    installed: null,
    installedNote: "Built locally or not a published release (no hash match).",
    latest: { version: "2026.6.1-ms7", tag: "v2026.6.1-ms7", publishedAt: "2026-10-09T10:20:56Z", releaseUrl: `${REL}/ApolloVibe/releases/tag/v2026.6.1-ms7` },
    announce: true,
    checkedAt: "2026-10-09T14:02:11Z",
    error: null,
    ...over,
  };
}

function moonlight(over: Partial<UpdateComponent> = {}): UpdateComponent {
  return {
    id: "moonlightvibe",
    name: "MoonlightVibe",
    status: "latestOnly",
    installed: null,
    installedNote: "Runs on your other devices. MultiSeat cannot see its version.",
    latest: { version: "6.3.10", tag: "v6.3.10", publishedAt: "2026-10-09T10:20:56Z", releaseUrl: `${REL}/MoonlightVibe/releases/tag/v6.3.10` },
    announce: true,
    checkedAt: "2026-10-09T14:02:11Z",
    error: null,
    ...over,
  };
}

function state(components: UpdateComponent[], over: Partial<UpdatesState> = {}): UpdatesState {
  return {
    enabled: true,
    intervalHours: 12,
    checkedAt: new Date(Date.now() - 2 * 3600_000).toISOString(),
    nextCheckAt: null,
    error: null,
    components,
    ...over,
  };
}

// ── fetch mock ──────────────────────────────────────────────────

type Handler = (init?: RequestInit) => { status: number; body?: unknown } | Promise<{ status: number; body?: unknown }>;
let routes: Record<string, Handler>;
let calls: { key: string; init?: RequestInit }[];

function json(status: number, body?: unknown) {
  return { status, body };
}

beforeEach(() => {
  localStorage.clear();
  calls = [];
  routes = {};
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? "GET"} ${url}`;
      calls.push({ key, init });
      const h = routes[key];
      if (!h) return new Response(JSON.stringify({ error: "not found" }), { status: 404 });
      const r = await h(init);
      return new Response(r.body === undefined ? null : JSON.stringify(r.body), {
        status: r.status,
        headers: { "Content-Type": "application/json" },
      });
    })
  );
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const GET = "GET /api/system/updates";
const CHECK = "POST /api/system/updates/check";
const SETTINGS = "POST /api/system/updates/settings";

function mount(ui: React.ReactNode = <><UpdateBanner /><UpdatesCard /></>) {
  return render(<UpdatesProvider>{ui}</UpdatesProvider>);
}

function banner() {
  return screen.queryByRole("region", { name: "Update notices" });
}

// ── Banner ──────────────────────────────────────────────────────

describe("banner wording per status", () => {
  it("shows the design 7.3 line for each announced component", async () => {
    routes[GET] = () => json(200, state([multiseat(), apollo(), moonlight()]));
    mount(<UpdateBanner />);
    const b = await screen.findByRole("region", { name: "Update notices" });
    expect(within(b).getByText("MultiSeat 0.6.19 is available. You have 0.6.18.")).toBeTruthy();
    expect(
      within(b).getByText(
        "ApolloVibe v2026.6.1-ms7 is available. This host's build could not be identified, so it may already be current."
      )
    ).toBeTruthy();
    expect(
      within(b).getByText(
        "New MoonlightVibe release 6.3.10 for your client devices. MultiSeat cannot see which version they run."
      )
    ).toBeTruthy();
    // How to update for the one we can compare, Release notes (a link) for the other two.
    expect(within(b).getByRole("button", { name: "How to update" })).toBeTruthy();
    expect(within(b).getAllByRole("link", { name: "Release notes" })).toHaveLength(2);
  });

  it("shows nothing for components that are not announced", async () => {
    routes[GET] = () => json(200, state([multiseat({ status: "upToDate", announce: false }), apollo({ announce: false })]));
    mount(<UpdateBanner />);
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    await act(async () => {});
    expect(banner()).toBeNull();
  });

  it("shows at most three lines", () => {
    const four = [multiseat(), apollo(), moonlight(), multiseat({ id: "extra", name: "Extra" })];
    expect(visibleAnnouncements(state(four), {})).toHaveLength(3);
  });

  it("uses only the informational palette, never an error banner", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    const { container } = mount(<UpdateBanner />);
    await screen.findByRole("region", { name: "Update notices" });
    expect(container.querySelector(".info-banner")).toBeTruthy();
    expect(container.querySelector(".error-banner")).toBeNull();
  });

  it("opens the text-only MultiSeat instructions from the banner", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    mount(<UpdateBanner />);
    fireEvent.click(await screen.findByRole("button", { name: "How to update" }));
    expect(screen.getByText(/install-service\.ps1 -FromZip/)).toBeTruthy();
  });
});

describe("dismissal", () => {
  it("hides a dismissed notice and stores it per component and version", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    mount(<UpdateBanner />);
    fireEvent.click(await screen.findByRole("button", { name: /Dismiss the MultiSeat notice/ }));
    expect(banner()).toBeNull();
    expect(JSON.parse(localStorage.getItem(DISMISS_KEY)!)).toEqual({ multiseat: "0.6.19" });
  });

  it("stays hidden for the dismissed version after a reload", async () => {
    localStorage.setItem(DISMISS_KEY, JSON.stringify({ multiseat: "0.6.19" }));
    routes[GET] = () => json(200, state([multiseat()]));
    mount(<UpdateBanner />);
    await waitFor(() => expect(calls.length).toBe(1));
    await act(async () => {});
    expect(banner()).toBeNull();
  });

  it("announces again when a newer version appears", async () => {
    localStorage.setItem(DISMISS_KEY, JSON.stringify({ multiseat: "0.6.19" }));
    const next = multiseat({
      latest: { version: "0.6.20", tag: "v0.6.20", publishedAt: "x", releaseUrl: `${REL}/MultiSeat/releases/tag/v0.6.20` },
    });
    routes[GET] = () => json(200, state([next]));
    mount(<UpdateBanner />);
    expect(await screen.findByText("MultiSeat 0.6.20 is available. You have 0.6.18.")).toBeTruthy();
  });

  it("dismissing one component leaves the others", async () => {
    routes[GET] = () => json(200, state([multiseat(), moonlight()]));
    mount(<UpdateBanner />);
    fireEvent.click(await screen.findByRole("button", { name: /Dismiss the MultiSeat notice/ }));
    expect(screen.queryByText(/MultiSeat 0\.6\.19 is available/)).toBeNull();
    expect(screen.getByText(/New MoonlightVibe release 6\.3\.10/)).toBeTruthy();
  });

  it("still renders, and still hides for the visit, when localStorage throws", async () => {
    const real = Storage.prototype;
    vi.spyOn(real, "getItem").mockImplementation(function (this: Storage, k: string) {
      if (k === DISMISS_KEY) throw new Error("blocked");
      return null;
    });
    vi.spyOn(real, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    routes[GET] = () => json(200, state([multiseat()]));
    mount(<UpdateBanner />);
    fireEvent.click(await screen.findByRole("button", { name: /Dismiss the MultiSeat notice/ }));
    expect(banner()).toBeNull();
  });
});

describe("silent failure", () => {
  it("shows nothing on a 401", async () => {
    routes[GET] = () => json(401, { error: "Unauthorized" });
    const { container } = mount();
    await waitFor(() => expect(calls.length).toBe(1));
    await act(async () => {});
    expect(banner()).toBeNull();
    expect(container.querySelector(".error-banner")).toBeNull();
    expect(screen.queryByText(/unauthori/i)).toBeNull();
  });

  it("shows nothing on a network error and does not retry in a loop", async () => {
    (fetch as unknown as ReturnType<typeof vi.fn>).mockRejectedValue(new TypeError("Failed to fetch"));
    const { container } = mount();
    await waitFor(() => expect((fetch as unknown as ReturnType<typeof vi.fn>).mock.calls.length).toBe(1));
    await act(async () => {});
    await new Promise((r) => setTimeout(r, 200));
    expect(banner()).toBeNull();
    expect(container.querySelector(".error-banner")).toBeNull();
    expect((fetch as unknown as ReturnType<typeof vi.fn>).mock.calls.length).toBe(1);
  });
});

describe("loading", () => {
  it("sends the saved API key like every other call", async () => {
    localStorage.setItem("multiseat-api-key", "k123");
    routes[GET] = () => json(200, state([]));
    mount();
    await waitFor(() => expect(calls.length).toBe(1));
    expect((calls[0].init?.headers as Record<string, string>)["X-MultiSeat-Key"]).toBe("k123");
  });

  it("reloads on window focus, but not twice in quick succession", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    routes[GET] = () => json(200, state([]));
    mount();
    await waitFor(() => expect(calls.length).toBe(1));
    fireEvent.focus(window);
    expect(calls.length).toBe(1);
    vi.setSystemTime(Date.now() + 11_000);
    fireEvent.focus(window);
    await waitFor(() => expect(calls.length).toBe(2));
  });
});

// ── System page card ────────────────────────────────────────────

describe("Updates card", () => {
  it("shows all three rows with installed, latest and status", async () => {
    routes[GET] = () => json(200, state([multiseat(), apollo(), moonlight()]));
    mount(<UpdatesCard />);
    const rows = await screen.findAllByText(/^installed:/);
    expect(rows).toHaveLength(3);
    expect(screen.getByText("installed: 0.6.18 (ebcd253)")).toBeTruthy();
    expect(screen.getByText("Update available")).toBeTruthy();
    expect(screen.getByText(/Last checked 2 h ago/)).toBeTruthy();
  });

  it("never calls an unidentified ApolloVibe build available or outdated", async () => {
    routes[GET] = () => json(200, state([apollo()]));
    const { container } = mount(<UpdatesCard />);
    await screen.findByText("installed: not identified");
    const row = container.querySelector('.update-row[data-component="apollovibe"]') as HTMLElement;
    expect(row.textContent).toContain("not identified");
    expect(row.textContent).toContain("Built locally or not a published release");
    expect(row.textContent).not.toMatch(/available|outdated/i);
  });

  it("labels MoonlightVibe as not tied to a specific client", async () => {
    routes[GET] = () => json(200, state([moonlight()]));
    mount(<UpdatesCard />);
    expect(await screen.findByText("Not tied to a specific client")).toBeTruthy();
  });

  it("renders server text as plain text, never as HTML", async () => {
    const evil = '<img src=x onerror="window.__pwned=1">';
    const hostile = apollo({
      installedNote: evil,
      latest: { version: evil, tag: evil, publishedAt: "x", releaseUrl: `${REL}/ApolloVibe/releases/tag/x` },
    });
    routes[GET] = () => json(200, state([hostile]));
    const { container } = mount();
    await screen.findByText("installed: not identified");
    expect(container.querySelector("img")).toBeNull();
    expect(container.textContent).toContain(evil);
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });

  it("gives the MultiSeat steps with -FromZip and the warnings, and nothing that runs a command", async () => {
    routes[GET] = () => json(200, state([multiseat(), apollo(), moonlight()]));
    const { container } = mount(<UpdatesCard />);
    await screen.findByText("installed: 0.6.18 (ebcd253)");
    const ms = container.querySelector('details[data-component="multiseat"]') as HTMLElement;
    expect(ms.textContent).toContain("-FromZip");
    const commands = Array.from(ms.querySelectorAll(".update-code")).map((e) => e.textContent);
    expect(commands).toContain(".\\scripts\\install-service.ps1 -FromZip .\\multiseat-windows-x64.zip");
    expect(ms.textContent).toContain("when nobody is streaming");
    expect(ms.textContent).toMatch(/Do not.*run a plain/s);
    expect(ms.textContent).toContain("Rebuild & Redeploy");
    const ap = container.querySelector('details[data-component="apollovibe"]') as HTMLElement;
    expect(ap.textContent).toContain("There is no update script yet");
    // Instruction panels hold text and links only.
    for (const panel of container.querySelectorAll(".update-instructions")) {
      expect(panel.querySelectorAll("button, form, input")).toHaveLength(0);
    }
  });

  it("when off, offers the one-click turn-on and says exactly what is sent", async () => {
    routes[GET] = () => json(200, state([multiseat({ status: "disabled", announce: false, latest: null })], { enabled: false }));
    routes[SETTINGS] = () => json(200, state([], { enabled: true }));
    mount(<UpdatesCard />);
    expect(await screen.findByText(/Update checks are off\. MultiSeat has not contacted the internet\./)).toBeTruthy();
    expect(screen.getByText(/Sends: GET api\.github\.com, generic User-Agent, nothing else\. Never downloads or installs anything\./)).toBeTruthy();
    expect(screen.getByText(/no\s+version, host name, user name, seat data or API key/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Check now" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Turn on update checks" }));
    await waitFor(() => expect(calls.some((c) => c.key === SETTINGS)).toBe(true));
    const post = calls.find((c) => c.key === SETTINGS)!;
    expect(JSON.parse(post.init!.body as string)).toEqual({ enabled: true });
  });

  it("when on, offers Turn off, which posts enabled false", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    routes[SETTINGS] = () => json(200, state([], { enabled: false }));
    mount(<UpdatesCard />);
    fireEvent.click(await screen.findByRole("button", { name: "Turn off" }));
    await waitFor(() => expect(calls.some((c) => c.key === SETTINGS)).toBe(true));
    expect(JSON.parse(calls.find((c) => c.key === SETTINGS)!.init!.body as string)).toEqual({ enabled: false });
  });

  it("Check now posts, refreshes and then waits out the cooldown", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    routes[CHECK] = () => json(202, state([multiseat()]));
    mount(<UpdatesCard />);
    const btn = await screen.findByRole("button", { name: "Check now" });
    fireEvent.click(btn);
    await waitFor(() => expect(calls.some((c) => c.key === CHECK)).toBe(true));
    await waitFor(() => expect((screen.getByRole("button", { name: "Check now" }) as HTMLButtonElement).disabled).toBe(true));
    expect(screen.getByText(/You can check again in a minute/)).toBeTruthy();
  });

  it("handles 409 (checks off) with a clear message", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    routes[CHECK] = () => json(409, { error: "Update checks are off" });
    mount(<UpdatesCard />);
    fireEvent.click(await screen.findByRole("button", { name: "Check now" }));
    expect(await screen.findByText("Update checks are off. Turn them on first.")).toBeTruthy();
  });

  it("handles 429 (cooldown) with a clear message and disables the button", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    routes[CHECK] = () => json(429, { error: "Too many" });
    mount(<UpdatesCard />);
    fireEvent.click(await screen.findByRole("button", { name: "Check now" }));
    expect(await screen.findByText("A check ran a moment ago. Try again in a minute.")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Check now" }) as HTMLButtonElement).disabled).toBe(true);
  });
});

// ── Release links ───────────────────────────────────────────────

describe("release links", () => {
  it.each([
    [`${REL}/MultiSeat/releases/tag/v0.6.19`, true],
    ["http://github.com/vibesoftwarecoder/MultiSeat/releases/tag/v1", false],
    ["https://github.com/evil/MultiSeat/releases/tag/v1", false],
    ["https://github.com/vibesoftwarecoder.evil.com/x", false],
    ["https://evil.example/https://github.com/vibesoftwarecoder/x", false],
    ["https://github.com/vibesoftwarecoder/../evil/x", false],
    ["https://github.com.evil.example/vibesoftwarecoder/x", false],
    ["https://user@github.com/vibesoftwarecoder/x", false],
    ["javascript:alert(1)", false],
    ["", false],
  ])("%s -> allowed %s", (url, ok) => {
    expect(safeReleaseUrl(url)).toBe(ok ? url : null);
  });

  it("rejects non-strings", () => {
    expect(safeReleaseUrl(undefined)).toBeNull();
    expect(safeReleaseUrl(42)).toBeNull();
  });

  it("opens allowed links in a new tab with noopener noreferrer", async () => {
    routes[GET] = () => json(200, state([moonlight()]));
    mount(<UpdateBanner />);
    const a = (await screen.findByRole("link", { name: "Release notes" })) as HTMLAnchorElement;
    expect(a.getAttribute("href")).toBe(`${REL}/MoonlightVibe/releases/tag/v6.3.10`);
    expect(a.getAttribute("target")).toBe("_blank");
    expect(a.getAttribute("rel")).toBe("noopener noreferrer");
  });

  it("renders no link at all for a hostile URL", async () => {
    const bad = moonlight({ latest: { version: "6.3.10", tag: "v6.3.10", publishedAt: "x", releaseUrl: "javascript:alert(1)" } });
    routes[GET] = () => json(200, state([bad]));
    const { container } = mount();
    await screen.findByRole("region", { name: "Update notices" });
    expect(container.querySelector("a")).toBeNull();
  });
});

// ── Settings and nav ────────────────────────────────────────────

describe("Settings > About", () => {
  it("shows the installed MultiSeat version", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    mount(<SettingsPage />);
    expect(await screen.findByText("0.6.18 (ebcd253)")).toBeTruthy();
    expect(screen.getByText("MultiSeat Version")).toBeTruthy();
  });

  it("omits it when the update data is unavailable", async () => {
    routes[GET] = () => json(401, { error: "x" });
    mount(<SettingsPage />);
    await waitFor(() => expect(calls.length).toBe(1));
    await act(async () => {});
    expect(screen.queryByText("MultiSeat Version")).toBeNull();
    expect(screen.getByText("API Endpoint")).toBeTruthy();
  });
});

describe("System nav dot", () => {
  it("shows while a notice is announced and goes away once dismissed", async () => {
    routes[GET] = () => json(200, state([multiseat()]));
    render(<App />);
    expect(await screen.findByRole("img", { name: "Update notice" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /Dismiss the MultiSeat notice/ }));
    expect(screen.queryByRole("img", { name: "Update notice" })).toBeNull();
  });

  it("is absent when nothing is announced", async () => {
    routes[GET] = () => json(200, state([multiseat({ announce: false, status: "upToDate" })]));
    render(<App />);
    await waitFor(() => expect(calls.some((c) => c.key === GET)).toBe(true));
    await act(async () => {});
    expect(screen.queryByRole("img", { name: "Update notice" })).toBeNull();
  });
});
