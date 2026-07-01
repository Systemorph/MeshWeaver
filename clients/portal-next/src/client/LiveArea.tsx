"use client";

// The SSR → live handoff for one mesh area.
//
// The async server component (app/[[...meshPath]]/AreaSnapshot.tsx) fetches the per-request
// snapshot and passes the synthesized {areas,data} tree as props into this client boundary. Next
// SSRs this component's INITIAL render into the streamed HTML — a MeshAreaView over a
// StaticAreaSource seeded from the snapshot — so the first paint is real, styled markup. After
// hydration the session's gRPC-web connection (LiveConnectionProvider) opens the node's default
// layout area; once the live stream has folded its first frame the displayed AreaSource swaps in
// place (same rootArea "", so the view simply rebinds). The server held no stream at any point;
// the live subscription exists only in the browser.

import { useMemo, useSyncExternalStore } from "react";
import { MessageBar, MessageBarBody } from "@fluentui/react-components";
import { MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import type { AreaSource, AreaTree } from "@meshweaver/react";
import { DEFAULT_ROOT_AREA } from "./live";
import { useLiveConnection } from "./LiveConnection";
import { useHydratedTheme } from "./useHydratedTheme";

export interface LiveAreaProps {
  /** The resolved mesh path (the URL path, or the user's home partition for the default route). */
  path: string;
  /** The SSR snapshot tree (null when no snapshot could be fetched — shell-only fallback). */
  initialTree: AreaTree | null;
  /** True when the server-side token mint failed (no authenticated session was forwarded). */
  unauthenticated?: boolean;
}

/** True once a source has folded its first Full frame (an empty tree is the pre-frame state). */
function hasContent(source: AreaSource): boolean {
  const state = source.getState();
  return state.areas != null && Object.keys(state.areas).length > 0;
}

const noSubscription = () => () => {};

export function LiveArea({ path, initialTree, unauthenticated }: LiveAreaProps) {
  const { theme } = useHydratedTheme();
  const live = useLiveConnection();

  // The SSR seed — one StaticAreaSource per snapshot; identity is stable across re-renders
  // because the RSC payload passes the same props object to every client render.
  const staticSource = useMemo(() => (initialTree ? new StaticAreaSource(initialTree) : null), [initialTree]);

  // The live source appears when the connection is up (created once per path, cached by the
  // provider for the connection's lifetime — instant back-nav).
  const liveSource = live.state.kind === "live" && path ? live.getAreaSource(path) : null;

  // Swap only when the live stream has actually delivered content — flipping to an empty live
  // source would blank the SSR snapshot. Server snapshot: always false (no stream on the server).
  const liveReady = useSyncExternalStore(
    liveSource ? liveSource.subscribe : noSubscription,
    () => (liveSource ? hasContent(liveSource) : false),
    () => false,
  );

  const active = liveReady && liveSource ? liveSource : staticSource;
  const streamError = live.areaErrors[path];
  const offline = live.state.kind === "offline";

  return (
    <div data-mw-live-area data-mw-path={path} data-mw-live={active != null && active === liveSource ? "true" : "false"} style={{ height: "100%" }}>
      {offline && (
        <div data-mw-offline style={{ marginBottom: 16 }}>
          <MessageBar intent="warning">
            <MessageBarBody>
              No live mesh connection on this origin ({(live.state as { reason: string }).reason}). Showing the
              server-rendered snapshot only.
            </MessageBarBody>
          </MessageBar>
        </div>
      )}
      {streamError && (
        <div data-mw-area-error style={{ marginBottom: 16 }}>
          <MessageBar intent="error">
            <MessageBarBody>Live area “{path}” failed: {streamError}</MessageBarBody>
          </MessageBar>
        </div>
      )}
      {unauthenticated && !active && (
        <MessageBar intent="info">
          <MessageBarBody>
            Not signed in — the server could not mint a mesh token from this session. Sign in to the portal on this
            origin, then reload.
          </MessageBarBody>
        </MessageBar>
      )}
      {active ? (
        <MeshAreaView
          source={active}
          rootArea={DEFAULT_ROOT_AREA}
          theme={theme}
          ops={live.state.kind === "live" ? live.state.mesh.ops : null}
        />
      ) : !unauthenticated ? (
        <div data-mw-connecting style={{ padding: 48, textAlign: "center", opacity: 0.7 }}>
          Connecting to the mesh…
        </div>
      ) : null}
    </div>
  );
}
