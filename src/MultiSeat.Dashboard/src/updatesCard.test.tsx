import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { UpdatesState } from "./api/types";
import { UpdatesProvider } from "./hooks/UpdatesContext";
import { UpdatesCard } from "./components/UpdatesCard";
import { UpdateOffNotice } from "./components/UpdateOffNotice";

function state(over: Partial<UpdatesState> = {}): UpdatesState {
  return {
    enabled: true,
    intervalHours: 12,
    checkedAt: null,
    nextCheckAt: null,
    error: null,
    components: [],
    ...over,
  };
}

let calls: string[];
let checkReply: (r: Response | Error) => void;
let serverEnabled = true;
let scrollSpy: ReturnType<typeof vi.fn>;

beforeEach(() => {
  vi.useFakeTimers();
  calls = [];
  serverEnabled = true;
  scrollSpy = vi.fn();
  Element.prototype.scrollIntoView = scrollSpy as unknown as typeof Element.prototype.scrollIntoView;
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string, init?: RequestInit) => {
      const key = `${init?.method ?? "GET"} ${url}`;
      calls.push(key);
      if (key === "POST /api/system/updates/check") {
        // The service runs the check inside this request; the test decides when it answers.
        return new Promise<Response>((resolve, reject) => {
          checkReply = (r) => (r instanceof Error ? reject(r) : resolve(r));
        });
      }
      return Promise.resolve(new Response(JSON.stringify(state({ enabled: serverEnabled })), { status: 200 }));
    })
  );
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const flush = () => act(async () => { await vi.advanceTimersByTimeAsync(0); });
const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms); });
const posts = () => calls.filter((c) => c === "POST /api/system/updates/check").length;
const checkBtn = () => screen.getByRole("button", { name: /Check now|Checking/ }) as HTMLButtonElement;

function mountCard(entry = "/") {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <UpdatesProvider>
        <UpdatesCard />
      </UpdatesProvider>
    </MemoryRouter>
  );
}

async function startCheck() {
  mountCard();
  await flush();
  await act(async () => { checkBtn().click(); });
}

describe("Check now while the service works", () => {
  it("shows Checking... and a disabled button for the whole 30 seconds, with one request", async () => {
    await startCheck();
    expect(checkBtn().textContent).toBe("Checking…");
    expect(checkBtn().disabled).toBe(true);
    await advance(30_000);
    expect(checkBtn().textContent).toBe("Checking…");
    expect(checkBtn().disabled).toBe(true);
    expect(posts()).toBe(1);
    expect(screen.getByText(/This can take up to 30 seconds/)).toBeTruthy();
  });

  it("a double click before React re-renders still sends one request", async () => {
    mountCard();
    await flush();
    const btn = checkBtn();
    await act(async () => {
      btn.click();
      btn.click();
    });
    expect(posts()).toBe(1);
  });

  it("returns to Check now after a slow success", async () => {
    await startCheck();
    await advance(30_000);
    await act(async () => { checkReply(new Response(JSON.stringify(state()), { status: 202 })); });
    await flush();
    await advance(60_000); // the one-minute cooldown after a check
    expect(checkBtn().textContent).toBe("Check now");
    expect(checkBtn().disabled).toBe(false);
  });

  it("re-enables the button with a plain message when the service fails", async () => {
    await startCheck();
    await advance(30_000);
    await act(async () => { checkReply(new Response(JSON.stringify({ error: "GitHub is unreachable" }), { status: 500 })); });
    await flush();
    expect(screen.getByText("GitHub is unreachable")).toBeTruthy();
    expect(checkBtn().textContent).toBe("Check now");
    expect(checkBtn().disabled).toBe(false);
  });

  it("re-enables the button when the request itself fails", async () => {
    await startCheck();
    await act(async () => { checkReply(new TypeError("Failed to fetch")); });
    await flush();
    expect(screen.getByText("Could not reach the service.")).toBeTruthy();
    expect(checkBtn().disabled).toBe(false);
  });

  it("still handles 409 and 429 after the wait", async () => {
    await startCheck();
    await act(async () => { checkReply(new Response(JSON.stringify({ error: "off" }), { status: 409 })); });
    await flush();
    expect(screen.getByText("Update checks are off. Turn them on first.")).toBeTruthy();
    expect(checkBtn().disabled).toBe(false);
    await act(async () => { checkBtn().click(); });
    await act(async () => { checkReply(new Response(JSON.stringify({ error: "slow down" }), { status: 429 })); });
    await flush();
    expect(screen.getByText("A check ran a moment ago. Try again in a minute.")).toBeTruthy();
    expect(checkBtn().disabled).toBe(true);
  });

  it("does not touch state or log errors when unmounted mid-request", async () => {
    const errors = vi.spyOn(console, "error").mockImplementation(() => {});
    const { unmount } = mountCard();
    await flush();
    await act(async () => { checkBtn().click(); });
    unmount();
    await act(async () => { checkReply(new Response(JSON.stringify(state()), { status: 202 })); });
    await advance(120_000);
    expect(errors).not.toHaveBeenCalled();
  });
});

describe("landing on the Updates card", () => {
  function app(entry: string) {
    return render(
      <MemoryRouter initialEntries={[entry]}>
        <UpdatesProvider>
          <UpdateOffNotice />
          <Routes>
            <Route path="/" element={<div>home</div>} />
            <Route path="/system" element={<UpdatesCard />} />
          </Routes>
        </UpdatesProvider>
      </MemoryRouter>
    );
  }

  it("scrolls to the card and focuses its heading when the URL hash is #updates", async () => {
    app("/system#updates");
    await flush();
    const card = document.getElementById("updates")!;
    expect(card).toBeTruthy();
    expect(scrollSpy.mock.contexts).toContain(card);
    expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Updates" }));
  });

  it("does the same when 'See what is sent' navigates there", async () => {
    serverEnabled = false; // the notice only shows while checks are off
    app("/");
    await flush();
    expect(scrollSpy).not.toHaveBeenCalled();
    await act(async () => { screen.getByRole("link", { name: "See what is sent" }).click(); });
    await flush();
    expect(screen.queryByText("home")).toBeNull();
    expect(scrollSpy.mock.contexts).toContain(document.getElementById("updates"));
    expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Updates" }));
  });

  it("does nothing without the hash", async () => {
    app("/system");
    await flush();
    expect(document.getElementById("updates")).toBeTruthy();
    expect(scrollSpy).not.toHaveBeenCalled();
  });

  it("copes when there is no card to land on", async () => {
    render(
      <MemoryRouter initialEntries={["/system#updates"]}>
        <UpdatesCard />
      </MemoryRouter>
    );
    await flush();
    expect(scrollSpy).not.toHaveBeenCalled();
  });
});
