"use client";

// The right-docked chat/content side panel — the React port of the Blazor shell's side panel
// (PortalLayoutBase splitter pane + SidePanel/SidePanelStateService):
//
//   - State {isVisible, width%, contentPath, title} persists across sessions (localStorage), the
//     same shape SidePanelStateService keeps. Default split 75/25; min 250px; max 85%.
//   - No content path → the new-chat composer (the ThreadChat control over the live ops surface,
//     seeded with the current node as its context — Blazor's GetSidePanelControl).
//   - A THREAD content path renders the thread's chat directly (a thread path IS its own node
//     address — no resolution round-trip, the CQRS rule the Blazor code pins).
//   - A non-thread content path renders that node's default layout area (the context peek).
//   - The header toggle is context-aware (PortalLayoutBase.ToggleSidePanel): on a thread in the
//     main view it peeks the thread's MAIN node; otherwise it toggles the new-chat composer.
//
// The AI menu's "New thread" action (area "ai-new-thread") opens the panel in new-chat mode —
// the same imperative handling as PortalLayoutBase.HandleMenuItemClick.

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { Button, Text } from "@fluentui/react-components";
import { Chat20Regular, Dismiss20Regular, PanelRightContract20Regular, PanelRightExpand20Regular } from "@fluentui/react-icons";
import { MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import type { AreaTree } from "@meshweaver/react";
import { useLiveConnection, useNavigationState } from "./LiveConnection";
import { useHydratedTheme } from "./useHydratedTheme";

const STORAGE_KEY = "mw-side-panel";
const THREAD_SEGMENT = "/_Thread/";

export interface SidePanelState {
  isVisible: boolean;
  /** Panel width as % of the main area (Blazor default 25). */
  width: number;
  contentPath: string | null;
  title: string | null;
}

const DEFAULT_STATE: SidePanelState = { isVisible: false, width: 25, contentPath: null, title: null };

interface SidePanelContextValue {
  state: SidePanelState;
  /** Context-aware header toggle (close / peek thread context / open new chat). */
  toggle(): void;
  /** Open the panel fresh in new-thread mode (the AI menu's "New thread"). */
  openNewThread(): void;
  close(): void;
  setWidth(width: number): void;
}

const SidePanelContext = createContext<SidePanelContextValue>({
  state: DEFAULT_STATE,
  toggle: () => {},
  openNewThread: () => {},
  close: () => {},
  setWidth: () => {},
});

export function useSidePanel(): SidePanelContextValue {
  return useContext(SidePanelContext);
}

function loadState(): SidePanelState {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULT_STATE;
    const parsed = JSON.parse(raw) as Partial<SidePanelState>;
    return {
      isVisible: parsed.isVisible === true,
      width: typeof parsed.width === "number" && parsed.width > 0 && parsed.width <= 85 ? parsed.width : 25,
      contentPath: typeof parsed.contentPath === "string" ? parsed.contentPath : null,
      title: typeof parsed.title === "string" ? parsed.title : null,
    };
  } catch {
    return DEFAULT_STATE;
  }
}

function isThreadPath(path: string | null | undefined): boolean {
  return !!path && path.toLowerCase().includes(THREAD_SEGMENT.toLowerCase());
}

export function SidePanelProvider({ children }: { children: ReactNode }) {
  const live = useLiveConnection();
  const nav = useNavigationState();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;
  const [state, setState] = useState<SidePanelState>(DEFAULT_STATE);

  // Restore the persisted state after mount (SSR renders the closed default), mirroring the
  // Blazor RestoreSidePanelStateAsync + the anonymous-circuit guard: never restore a visible
  // panel without a live authenticated connection (the content needs the workspace).
  useEffect(() => {
    setState(loadState());
  }, []);
  useEffect(() => {
    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
    } catch {
      // storage unavailable (private mode) — panel state is session-only then
    }
  }, [state]);

  // The current main-view node — used for the thread-context peek. The nav target address is a
  // node address (resolved server-side); its mainNode arrives off the node read.
  const currentAddress = nav.target?.address ?? "";
  const [mainNodeOfThread, setMainNodeOfThread] = useState<string | null>(null);
  useEffect(() => {
    setMainNodeOfThread(null);
    if (!mesh || !isThreadPath(currentAddress)) return;
    let liveFlag = true;
    mesh.getNode(currentAddress).then((node) => {
      if (!liveFlag || !node) return;
      const main = typeof node.mainNode === "string" ? node.mainNode : "";
      if (main && main.toLowerCase() !== currentAddress.toLowerCase()) setMainNodeOfThread(main);
    });
    return () => {
      liveFlag = false;
    };
  }, [mesh, currentAddress]);

  const toggle = useCallback(() => {
    setState((s) => {
      // On a thread in the main view → the panel is a peek of the thread's context node.
      if (mainNodeOfThread) {
        if (s.isVisible) return { ...s, isVisible: false };
        return {
          ...s,
          isVisible: true,
          contentPath: mainNodeOfThread,
          title: mainNodeOfThread.split("/").pop() ?? mainNodeOfThread,
        };
      }
      return { ...s, isVisible: !s.isVisible };
    });
  }, [mainNodeOfThread]);

  const openNewThread = useCallback(() => {
    setState((s) => ({ ...s, isVisible: true, contentPath: null, title: null }));
  }, []);

  const close = useCallback(() => setState((s) => ({ ...s, isVisible: false })), []);
  const setWidth = useCallback(
    (width: number) => setState((s) => ({ ...s, width: Math.min(85, Math.max(10, width)) })),
    [],
  );

  const value = useMemo(
    () => ({ state, toggle, openNewThread, close, setWidth }),
    [state, toggle, openNewThread, close, setWidth],
  );

  return (
    <SidePanelContext.Provider value={value}>
      {children}
    </SidePanelContext.Provider>
  );
}

/** The context-aware header toggle button (PortalLayoutBase side-panel toggle). */
export function SidePanelToggle() {
  const { state, toggle } = useSidePanel();
  const nav = useNavigationState();
  const onThread = isThreadPath(nav.target?.address);
  const title = state.isVisible ? "Close side panel" : onThread ? "Show context" : "Chat";
  return (
    <Button
      appearance="transparent"
      title={title}
      aria-label={title}
      onClick={toggle}
      icon={
        state.isVisible ? (
          <PanelRightContract20Regular />
        ) : onThread ? (
          <PanelRightExpand20Regular />
        ) : (
          <Chat20Regular />
        )
      }
    />
  );
}

/** The docked panel itself — render inside the shell's main grid row, next to the content. */
export function SidePanelPane() {
  const { state, close, setWidth } = useSidePanel();
  const live = useLiveConnection();
  const nav = useNavigationState();
  const { theme } = useHydratedTheme();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;

  // New-chat composer: a ThreadChat control over the live ops surface, seeded with the current
  // node as context. Keyed on the CONTENT path only — never the navigation path (the Blazor
  // side-panel keying rule: rebuilding per navigation destroys the in-progress conversation).
  const composerSource = useMemo(() => {
    if (state.contentPath) return null;
    const tree: AreaTree = {
      areas: {
        "": {
          $type: "ThreadChat",
          threadPath: "",
          initialContext: nav.target?.address ?? "",
        },
      },
    };
    return new StaticAreaSource(tree);
    // Deliberately NOT keyed on nav — the composer keeps its identity across navigation;
    // the initial context seeds once (Blazor's WithInitialContext).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.contentPath, mesh]);

  // Thread content: the ThreadChat control bound to the thread path (a thread path is its own
  // node address — direct render, no resolution).
  const threadSource = useMemo(() => {
    if (!state.contentPath || !isThreadPath(state.contentPath)) return null;
    const tree: AreaTree = {
      areas: { "": { $type: "ThreadChat", threadPath: state.contentPath } },
    };
    return new StaticAreaSource(tree);
  }, [state.contentPath]);

  // Non-thread content: the node's default layout area (context peek) over the shared source cache.
  const contentTarget =
    state.contentPath && !isThreadPath(state.contentPath)
      ? { address: state.contentPath, area: "", id: "" }
      : null;
  const contentSource = contentTarget ? live.getAreaSource(contentTarget) : null;

  // Drag-to-resize off the panel's left edge (the splitter bar).
  const dragging = useRef(false);
  useEffect(() => {
    const onMove = (e: MouseEvent) => {
      if (!dragging.current) return;
      const pct = ((window.innerWidth - e.clientX) / window.innerWidth) * 100;
      setWidth(pct);
    };
    const onUp = () => {
      dragging.current = false;
      document.body.style.userSelect = "";
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
  }, [setWidth]);

  if (!state.isVisible || !mesh) return null;

  const source = composerSource ?? threadSource ?? contentSource;

  return (
    <aside
      data-mw-side-panel
      style={{
        width: `${state.width}%`,
        minWidth: 250,
        maxWidth: "85%",
        borderLeft: "1px solid var(--colorNeutralStroke2)",
        display: "flex",
        flexDirection: "column",
        minHeight: 0,
        position: "relative",
      }}
    >
      <div
        data-mw-side-panel-resizer
        onMouseDown={() => {
          dragging.current = true;
          document.body.style.userSelect = "none";
        }}
        style={{ position: "absolute", left: -4, top: 0, bottom: 0, width: 8, cursor: "col-resize", zIndex: 10 }}
      />
      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: 8,
          padding: "6px 8px",
          borderBottom: "1px solid var(--colorNeutralStroke2)",
        }}
      >
        <Text weight="semibold" size={300} style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
          {state.title ?? (state.contentPath ? state.contentPath.split("/").pop() : "New chat")}
        </Text>
        <Button appearance="transparent" icon={<Dismiss20Regular />} aria-label="Close side panel" onClick={close} />
      </div>
      <div style={{ flex: 1, minHeight: 0, overflow: "auto", padding: 8 }}>
        {source ? (
          <MeshAreaView source={source} rootArea="" theme={theme} ops={mesh.ops} />
        ) : (
          <Text size={200} style={{ padding: 12, color: "var(--colorNeutralForeground3)" }}>
            Connecting…
          </Text>
        )}
      </div>
    </aside>
  );
}
