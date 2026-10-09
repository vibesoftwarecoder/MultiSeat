import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { UpdateComponent, UpdatesState } from "./api/types";
import { UpdatesProvider } from "./hooks/UpdatesContext";
import { UpdateBanner } from "./components/UpdateBanner";
import { UpdateOffNotice } from "./components/UpdateOffNotice";
import { OFF_NOTICE_DISMISS_KEY } from "./components/updateUtils";

const REL = "https://github.com/vibesoftwarecoder";

function comp(id: string, name: string): UpdateComponent {
  return {
    id,
    name,
    status: "updateAvailable",
    installed: { version: "1.0.0", display: "1.0.0", source: "assembly", note: null },
    latest: { version: "2.0.0", tag: "v2.0.0", publishedAt: "x", releaseUrl: `${REL}/${name}/releases/tag/v2.0.0` },
    announce: true,
    checkedAt: null,
    error: null,
  };
}

function state(over: Partial<UpdatesState> = {}): UpdatesState {
  return { enabled: false, intervalHours: 12, checkedAt: null, nextCheckAt: null, error: null, components: [], ...over };
}

let respond: () => Promise<Response>;
let calls: string[];

beforeEach(() => {
  localStorage.clear();
  calls = [];
  respond = async () => new Response(JSON.stringify(state()), { status: 200 });
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? "GET"} ${url}`);
      return respond();
    })
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function mount() {
  return render(
    <MemoryRouter initialEntries={["/"]}>
      <UpdatesProvider>
        <UpdateBanner />
        <UpdateOffNotice />
        <Routes>
          <Route path="/" element={<div>home page</div>} />
          <Route path="/system" element={<div>system page</div>} />
        </Routes>
      </UpdatesProvider>
    </MemoryRouter>
  );
}

const notice = () => screen.queryByRole("region", { name: "Update checks are off" });
const settle = async () => {
  await waitFor(() => expect(calls.length).toBeGreaterThan(0));
  await act(async () => {});
};

describe("update checks are off notice", () => {
  it("shows when the data loaded, checks are off and it was not dismissed", async () => {
    mount();
    expect(await screen.findByRole("region", { name: "Update checks are off" })).toBeTruthy();
  });

  it("says what the design says, including that nothing has contacted the internet", async () => {
    mount();
    const n = await screen.findByRole("region", { name: "Update checks are off" });
    expect(n.textContent).toContain("Update checks are off. MultiSeat has not contacted the internet.");
    expect(n.textContent).toContain("a new MultiSeat, ApolloVibe or MoonlightVibe release is out");
  });

  it("is hidden when checks are on", async () => {
    respond = async () => new Response(JSON.stringify(state({ enabled: true })), { status: 200 });
    mount();
    await settle();
    expect(notice()).toBeNull();
  });

  it("is hidden while the data is still loading", async () => {
    respond = () => new Promise<Response>(() => {});
    mount();
    await settle();
    expect(notice()).toBeNull();
  });

  it("is hidden on a 401", async () => {
    respond = async () => new Response(JSON.stringify({ error: "Unauthorized" }), { status: 401 });
    mount();
    await settle();
    expect(notice()).toBeNull();
  });

  it("is hidden on a network error", async () => {
    respond = async () => {
      throw new TypeError("Failed to fetch");
    };
    mount();
    await settle();
    expect(notice()).toBeNull();
  });

  it("'Not now' hides it, stores the flag, and it stays hidden after a reload", async () => {
    const first = mount();
    fireEvent.click(await screen.findByRole("button", { name: "Not now" }));
    expect(notice()).toBeNull();
    expect(localStorage.getItem(OFF_NOTICE_DISMISS_KEY)).toBe("1");
    first.unmount();
    calls = [];
    mount();
    await settle();
    expect(notice()).toBeNull();
  });

  it("stays hidden if checks are turned on and then off again after a dismissal", async () => {
    localStorage.setItem(OFF_NOTICE_DISMISS_KEY, "1");
    mount();
    await settle();
    expect(notice()).toBeNull();
  });

  it("still renders, and hides for the visit, when localStorage throws", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(function (this: Storage, k: string) {
      if (k === OFF_NOTICE_DISMISS_KEY) throw new Error("blocked");
      return null;
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    mount();
    fireEvent.click(await screen.findByRole("button", { name: "Not now" }));
    expect(notice()).toBeNull();
  });

  it("'See what is sent' goes to the System page", async () => {
    mount();
    fireEvent.click(await screen.findByRole("link", { name: "See what is sent" }));
    expect(await screen.findByText("system page")).toBeTruthy();
  });

  it("has no control that turns checks on, and sends no write on its own", async () => {
    mount();
    const n = await screen.findByRole("region", { name: "Update checks are off" });
    const labels = Array.from(n.querySelectorAll("a, button")).map((e) => e.textContent);
    expect(labels).toEqual(["See what is sent", "Not now"]);
    fireEvent.click(screen.getByRole("button", { name: "Not now" }));
    fireEvent.click(n.querySelector("a")!);
    expect(calls.filter((c) => c.startsWith("POST"))).toEqual([]);
  });

  it("does not count toward the three update lines", async () => {
    const four = [comp("multiseat", "MultiSeat"), comp("apollovibe", "ApolloVibe"), comp("moonlightvibe", "MoonlightVibe")];
    respond = async () => new Response(JSON.stringify(state({ components: four })), { status: 200 });
    const { container } = mount();
    await screen.findByRole("region", { name: "Update checks are off" });
    expect(container.querySelectorAll('.update-banner[data-component]')).toHaveLength(3);
  });
});
