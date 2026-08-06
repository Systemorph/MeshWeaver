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
import { useRouter } from "next/navigation";
import { Button, Spinner, Text } from "@fluentui/react-components";
import {
  Add20Regular,
  ArrowCounterclockwise20Regular,
  ArrowMaximize20Regular,
  Chat20Regular,
  Dismiss20Regular,
  PanelRightContract20Regular,
  PanelRightExpand20Regular,
} from "@fluentui/react-icons";
import { MeshAreaView, StaticAreaSource, useLocalize } from "@meshweaver/react";
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
  /**
   * Chat vs the recent-threads picker. Transient (never persisted) — the Blazor twin is
   * ThreadChatView's `viewMode`, driven by SidePanelState.RequestAction("New"|"Resume"), which is
   * likewise per-session.
   */
  mode: "chat" | "resume";
}

const DEFAULT_STATE: SidePanelState = { isVisible: false, width: 25, contentPath: null, title: null, mode: "chat" };

interface SidePanelContextValue {
  state: SidePanelState;
  /** Context-aware header toggle (close / peek thread context / open new chat). */
  toggle(): void;
  /** Open the panel fresh in new-thread mode (the AI menu's "New thread"). */
  openNewThread(): void;
  /** Show the recent-threads picker (SidePanel's ↺ / RequestAction("Resume")). */
  resumeThread(): void;
  /** Pick a thread out of the resume list, or peek a node. */
  setContent(path: string | null, title?: string | null): void;
  /** Move the panel's thread into the main view (SidePanel's ⤢ MoveToMainPanel). */
  moveToMainPanel(): void;
  close(): void;
  setWidth(width: number): void;
}

const SidePanelContext = createContext<SidePanelContextValue>({
  state: DEFAULT_STATE,
  toggle: () => {},
  openNewThread: () => {},
  resumeThread: () => {},
  setContent: () => {},
  moveToMainPanel: () => {},
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
      mode: "chat", // never restored — a reload always lands on chat, as in Blazor
    };
  } catch {
    return DEFAULT_STATE;
  }
}

function isThreadPath(path: string | null | undefined): boolean {
  return !!path && path.toLowerCase().includes(THREAD_SEGMENT.toLowerCase());
}

/**
 * The namespace the resume list is scoped to: the partition the current address sits in — i.e. the
 * address itself for a partition root, and everything before `/_Thread/` when already on a thread.
 */
export function namespaceOf(address: string): string {
  if (!address) return "";
  const i = address.toLowerCase().indexOf(THREAD_SEGMENT.toLowerCase());
  if (i >= 0) return address.slice(0, i);
  return address.split("/")[0] ?? "";
}

export function SidePanelProvider({ children }: { children: ReactNode }) {
  const live = useLiveConnection();
  const nav = useNavigationState();
  const router = useRouter();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;
  const [state, setState] = useState<SidePanelState>(DEFAULT_STATE);
  // MoveToMainPanel needs the CURRENT content path outside the setState updater (it navigates as a
  // side effect); a ref keeps that read from going stale in the callback's closure.
  const stateRef = useRef(state);
  stateRef.current = state;

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

  // Clearing the content path is what makes the new-chat composer render — the same reason Blazor's
  // OnNewThread calls SetContentPath(null) in the always-mounted panel rather than only raising
  // RequestAction("New"): with a thread displayed, no composer is subscribed to the action, so the
  // click would otherwise do nothing ("clicking + keeps me on the thread").
  const openNewThread = useCallback(() => {
    setState((s) => ({ ...s, isVisible: true, contentPath: null, title: null, mode: "chat" }));
  }, []);

  const resumeThread = useCallback(() => {
    setState((s) => ({ ...s, isVisible: true, mode: "resume" }));
  }, []);

  const setContent = useCallback((path: string | null, title?: string | null) => {
    setState((s) => ({
      ...s,
      isVisible: true,
      contentPath: path,
      title: title ?? (path ? (path.split("/").pop() ?? path) : null),
      mode: "chat",
    }));
  }, []);

  // Hand the panel's thread to the main view and close the panel (Blazor's MoveToMainPanel:
  // clear the content, hide, then NavigateTo($"/{contentPath}")).
  const moveToMainPanel = useCallback(() => {
    const path = stateRef.current.contentPath;
    setState((s) => ({ ...s, isVisible: false, contentPath: null, title: null, mode: "chat" }));
    if (path) router.push(`/${path}`);
  }, [router]);

  const close = useCallback(() => setState((s) => ({ ...s, isVisible: false })), []);
  const setWidth = useCallback(
    (width: number) => setState((s) => ({ ...s, width: Math.min(85, Math.max(10, width)) })),
    [],
  );

  const value = useMemo(
    () => ({ state, toggle, openNewThread, resumeThread, setContent, moveToMainPanel, close, setWidth }),
    [state, toggle, openNewThread, resumeThread, setContent, moveToMainPanel, close, setWidth],
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
  const t = useLocalize();
  const onThread = isThreadPath(nav.target?.address);
  const title = state.isVisible ? t("chat.closeSidePanel") : onThread ? t("chat.showContext") : t("chat.chat");
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

/**
 * The recent-threads picker — the port of ThreadChatView's ResumeThreads mode. Same query the
 * Blazor SwitchToResumeModeAsync builds: threads under the current namespace's `_Thread`, newest
 * first, falling back to an unscoped thread query when there is no namespace.
 */
function ResumeThreadList({ namespace, onPick }: { namespace: string; onPick: (path: string, title: string) => void }) {
  const live = useLiveConnection();
  const t = useLocalize();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;
  const [rows, setRows] = useState<{ path: string; name: string }[] | null>(null);

  useEffect(() => {
    if (!mesh?.ops?.search) return;
    let alive = true;
    const query = namespace
      ? `nodeType:Thread namespace:${namespace}/_Thread sort:LastModified-desc`
      : "nodeType:Thread sort:LastModified-desc";
    mesh.ops
      .search(query, undefined, 50)
      .then((rs) => {
        if (!alive) return;
        setRows(
          rs
            .map((r) => ({ path: String(r.path ?? ""), name: String(r.name ?? "") }))
            .filter((r) => r.path.length > 0),
        );
      })
      .catch(() => alive && setRows([]));
    return () => {
      alive = false;
    };
  }, [mesh, namespace]);

  if (rows == null) return <Spinner size="tiny" />;
  if (rows.length === 0)
    return (
      <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
        {t("chat.noThreadsYet")}
      </Text>
    );
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 4 }} data-mw-resume-list>
      {rows.map((r) => (
        <Button
          key={r.path}
          appearance="subtle"
          style={{ justifyContent: "flex-start" }}
          onClick={() => onPick(r.path, r.name || (r.path.split("/").pop() ?? r.path))}
        >
          {r.name || r.path.split("/").pop()}
        </Button>
      ))}
    </div>
  );
}

/** The docked panel itself — render inside the shell's main grid row, next to the content. */
export function SidePanelPane() {
  const { state, close, setWidth, openNewThread, resumeThread, setContent, moveToMainPanel } = useSidePanel();
  const t = useLocalize();
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
        {/* The four header actions of Blazor's SidePanel.razor: new thread, resume, open in the
            main panel (only with a thread), close. */}
        <Button
          appearance="transparent"
          icon={<Add20Regular />}
          title={t("chat.new")}
          aria-label={t("chat.new")}
          onClick={openNewThread}
        />
        <Button
          appearance="transparent"
          icon={<ArrowCounterclockwise20Regular />}
          title={t("chat.resume")}
          aria-label={t("chat.resume")}
          onClick={resumeThread}
        />
        <Text weight="semibold" size={300} style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
          {state.mode === "resume"
            ? t("chat.recentThreads")
            : (state.title ?? (state.contentPath ? state.contentPath.split("/").pop() : t("chat.new")))}
        </Text>
        {state.contentPath ? (
          <Button
            appearance="transparent"
            icon={<ArrowMaximize20Regular />}
            title={t("chat.openInMainPanel")}
            aria-label={t("chat.openInMainPanel")}
            onClick={moveToMainPanel}
          />
        ) : null}
        <Button
          appearance="transparent"
          icon={<Dismiss20Regular />}
          title={t("chat.closeSidePanel")}
          aria-label={t("chat.closeSidePanel")}
          onClick={close}
        />
      </div>
      <div style={{ flex: 1, minHeight: 0, overflow: "auto", padding: 8 }}>
        {state.mode === "resume" ? (
          <ResumeThreadList
            namespace={namespaceOf(nav.target?.address ?? "")}
            onPick={(path, title) => setContent(path, title)}
          />
        ) : source ? (
          <MeshAreaView source={source} rootArea="" theme={theme} ops={mesh.ops} />
        ) : (
          <Text size={200} style={{ padding: 12, color: "var(--colorNeutralForeground3)" }}>
            {t("ui.connecting")}
          </Text>
        )}
      </div>
    </aside>
  );
}
