// @vitest-environment jsdom
// The side panel's header actions — the four Blazor SidePanel.razor buttons (new thread, resume,
// open in main panel, close) and the state transitions behind them. portal-next shipped only
// "close", so a user could neither start a second thread from the panel nor promote the panel's
// thread into the main view without hand-editing the URL.

import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { localize } from "@meshweaver/react";
import { namespaceOf, SidePanelProvider, SidePanelPane, useSidePanel } from "../src/client/SidePanel";

// Look buttons up by their CATALOG text, not a literal — that also pins that the labels are
// localized (a hard-coded English label would no longer match once the key's text changes).
const en = (key: string) => localize(key, "en");

const push = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push, replace: () => {}, refresh: () => {}, back: () => {}, forward: () => {}, prefetch: () => {} }),
  usePathname: () => "/",
  useSearchParams: () => new URLSearchParams(),
}));

const search = vi.fn(async () => [
  { path: "acme/_Thread/t1", name: "Pricing question" },
  { path: "acme/_Thread/t2", name: "Margin review" },
]);

const mesh = {
  ops: { search, watch: () => ({ async *[Symbol.asyncIterator]() {} }), patch: () => {} },
  getNode: async () => null,
  userId: "u1",
};

vi.mock("../src/client/LiveConnection", () => ({
  useLiveConnection: () => ({ state: { kind: "live", mesh }, areaErrors: {}, getAreaSource: () => null }),
  useNavigationState: () => ({ path: "/acme/Docs", target: { address: "acme/Docs", area: "", id: "" } }),
}));

vi.mock("../src/client/useHydratedTheme", () => ({ useHydratedTheme: () => ({ theme: {} }) }));

// The panel body renders the ThreadChat control through MeshAreaView; the header + resume list are
// what this file is about, so the area view is stubbed to a marker.
vi.mock("@meshweaver/react", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@meshweaver/react");
  return { ...actual, MeshAreaView: () => <div data-testid="area-view" /> };
});

/** Drives the context from inside the provider so the tests can assert state transitions. */
let ctx: ReturnType<typeof useSidePanel>;
function Probe() {
  ctx = useSidePanel();
  return null;
}

function mount() {
  return render(
    <SidePanelProvider>
      <Probe />
      <SidePanelPane />
    </SidePanelProvider>,
  );
}

// This jsdom build exposes no localStorage (the panel guards every access in try/catch, so it runs
// without one). The persistence assertions need a real store, so install a minimal one.
if (!("localStorage" in window) || !window.localStorage) {
  const store = new Map<string, string>();
  Object.defineProperty(window, "localStorage", {
    configurable: true,
    value: {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => void store.set(k, String(v)),
      removeItem: (k: string) => void store.delete(k),
      clear: () => store.clear(),
      key: (i: number) => [...store.keys()][i] ?? null,
      get length() {
        return store.size;
      },
    },
  });
}

beforeEach(() => {
  push.mockClear();
  search.mockClear();
  window.localStorage.clear();
});
afterEach(cleanup);

describe("namespaceOf — what the resume list is scoped to", () => {
  it("takes the partition of a plain node address", () => {
    expect(namespaceOf("acme/Docs/Guide")).toBe("acme");
  });
  it("takes everything before /_Thread/ when already on a thread", () => {
    expect(namespaceOf("acme/Sales/_Thread/t1")).toBe("acme/Sales");
  });
  it("is empty for an empty address", () => {
    expect(namespaceOf("")).toBe("");
  });
});

describe("side panel header actions", () => {
  it("renders all four Blazor actions once a thread is open", async () => {
    mount();
    await act(async () => ctx.setContent("acme/_Thread/t1", "Pricing question"));
    expect(screen.getByLabelText(en("chat.new"))).toBeTruthy();
    expect(screen.getByLabelText(en("chat.resume"))).toBeTruthy();
    expect(screen.getByLabelText(en("chat.openInMainPanel"))).toBeTruthy();
    expect(screen.getByLabelText(en("chat.closeSidePanel"))).toBeTruthy();
  });

  it("hides 'open in main panel' when the panel holds no content (the new-chat composer)", async () => {
    mount();
    await act(async () => ctx.openNewThread());
    expect(screen.queryByLabelText(en("chat.openInMainPanel"))).toBeNull();
    expect(screen.getByLabelText(en("chat.new"))).toBeTruthy();
  });

  // The Blazor rule: OnNewThread CLEARS the content path in the always-mounted panel, because with
  // a thread displayed no composer is subscribed to RequestAction("New") and the click would
  // otherwise do nothing ("clicking + keeps me on the thread").
  it("new thread clears the content path so the composer renders", async () => {
    mount();
    await act(async () => ctx.setContent("acme/_Thread/t1", "Pricing question"));
    expect(ctx.state.contentPath).toBe("acme/_Thread/t1");
    await act(async () => ctx.openNewThread());
    expect(ctx.state.contentPath).toBeNull();
    expect(ctx.state.title).toBeNull();
    expect(ctx.state.mode).toBe("chat");
    expect(ctx.state.isVisible).toBe(true);
  });

  it("open-in-main-panel navigates to the thread and closes the panel", async () => {
    mount();
    await act(async () => ctx.setContent("acme/_Thread/t1", "Pricing question"));
    await act(async () => ctx.moveToMainPanel());
    expect(push).toHaveBeenCalledWith("/acme/_Thread/t1");
    expect(ctx.state.isVisible).toBe(false);
    expect(ctx.state.contentPath).toBeNull();
  });

  it("open-in-main-panel does nothing when there is no content path", async () => {
    mount();
    await act(async () => ctx.openNewThread());
    await act(async () => ctx.moveToMainPanel());
    expect(push).not.toHaveBeenCalled();
  });
});

describe("resume mode", () => {
  it("queries the current namespace's threads, newest first", async () => {
    mount();
    await act(async () => ctx.resumeThread());
    await act(async () => {
      await Promise.resolve();
    });
    expect(search).toHaveBeenCalledWith("nodeType:Thread namespace:acme/_Thread sort:LastModified-desc", undefined, 50);
  });

  it("lists the threads and picking one shows it in the panel", async () => {
    mount();
    await act(async () => ctx.resumeThread());
    await act(async () => {
      await Promise.resolve();
    });
    expect(screen.getByText(en("chat.recentThreads"))).toBeTruthy();
    const item = screen.getByText("Pricing question");
    await act(async () => item.click());
    expect(ctx.state.contentPath).toBe("acme/_Thread/t1");
    expect(ctx.state.mode).toBe("chat");
  });

  it("is never restored from storage — a reload always lands on chat", async () => {
    window.localStorage.setItem(
      "mw-side-panel",
      JSON.stringify({ isVisible: true, width: 25, contentPath: null, title: null, mode: "resume" }),
    );
    mount();
    await act(async () => {
      await Promise.resolve();
    });
    expect(ctx.state.mode).toBe("chat");
  });
});
