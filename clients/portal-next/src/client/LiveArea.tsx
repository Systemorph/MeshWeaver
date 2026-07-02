"use client";

// The SSR → live handoff for one mesh area.
//
// The async server component (app/[[...meshPath]]/AreaSnapshot.tsx) fetches the per-request
// snapshot AND the path's navigation resolution (node address + area remainder), passing both as
// props into this client boundary. Next SSRs this component's INITIAL render into the streamed
// HTML — a MeshAreaView over a StaticAreaSource seeded from the snapshot — so the first paint is
// real, styled markup. After hydration the session's gRPC-web connection (LiveConnectionProvider)
// opens the RESOLVED node's layout area; once the live stream has folded its first frame the
// displayed AreaSource swaps in place. The server held no stream at any point; the live
// subscription exists only in the browser.
//
// Nested LayoutAreaControl embeds (the user home's Pinned/Threads/Catalog/Composer regions, doc
// embeds, …) hydrate through EmbeddedAreaProvider — each opens its own live source over the same
// connection, exactly like the Vite SPA.

import { useEffect, useMemo, useSyncExternalStore } from "react";
import { MessageBar, MessageBarBody } from "@fluentui/react-components";
import { EmbeddedAreaProvider, GrpcAreaSource, MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import type { AreaSource, AreaTree } from "@meshweaver/react";
import { rootAreaOf, targetKey, type AreaTarget } from "./live";
import { useLiveConnection, usePublishNavigation } from "./LiveConnection";
import { useHydratedTheme } from "./useHydratedTheme";

export interface LiveAreaProps {
  /** The URL mesh path as routed (or the user's home partition for the default route). */
  path: string;
  /** The resolved live-subscription target (node address + area/id split off the URL). */
  target: AreaTarget;
  /** The SSR snapshot tree (null when no snapshot could be fetched — shell-only fallback). */
  initialTree: AreaTree | null;
  /** Root key of the SSR snapshot tree ("" for the default-area frame and the preview tree;
   *  the area name for an explicit-area frame). */
  initialRootArea?: string;
  /** True when the server-side token mint failed (no authenticated session was forwarded). */
  unauthenticated?: boolean;
}

/** True once a source has folded its first Full frame (an empty tree is the pre-frame state). */
function hasContent(source: AreaSource): boolean {
  const state = source.getState();
  return state.areas != null && Object.keys(state.areas).length > 0;
}

const noSubscription = () => () => {};

export function LiveArea({ path, target, initialTree, initialRootArea = "", unauthenticated }: LiveAreaProps) {
  const { theme } = useHydratedTheme();
  const live = useLiveConnection();
  const publishNavigation = usePublishNavigation();

  // Publish the current target to the shell chrome (menus, settings, search scoping) — the React
  // twin of Blazor's NavigationService context. Effect-scoped: runs after hydration, per nav.
  useEffect(() => {
    publishNavigation({ path, target });
  }, [path, target, publishNavigation]);

  // The SSR seed — one StaticAreaSource per snapshot; identity is stable across re-renders
  // because the RSC payload passes the same props object to every client render.
  const staticSource = useMemo(() => (initialTree ? new StaticAreaSource(initialTree) : null), [initialTree]);

  // The live source appears when the connection is up (created once per target, cached by the
  // provider for the connection's lifetime — instant back-nav).
  const liveSource = live.state.kind === "live" && target.address ? live.getAreaSource(target) : null;

  // Swap only when the live stream has actually delivered content — flipping to an empty live
  // source would blank the SSR snapshot. Server snapshot: always false (no stream on the server).
  const liveReady = useSyncExternalStore(
    liveSource ? liveSource.subscribe : noSubscription,
    () => (liveSource ? hasContent(liveSource) : false),
    () => false,
  );

  const active = liveReady && liveSource ? liveSource : staticSource;
  const isLive = active != null && active === liveSource;
  const streamError = live.areaErrors[targetKey(target)];
  const offline = live.state.kind === "offline";
  const connection = live.state.kind === "live" ? live.state.mesh.connection : null;

  // The live source roots at the explicit area name when the URL addressed one; the SSR seed
  // roots wherever the server said its tree roots (the rendered frame's area, or "" for the
  // synthesized preview).
  const rootArea = isLive ? rootAreaOf(target) : initialRootArea;

  const view = active ? (
    <MeshAreaView
      source={active}
      rootArea={rootArea}
      theme={theme}
      ops={live.state.kind === "live" ? live.state.mesh.ops : null}
    />
  ) : null;

  return (
    <div
      data-mw-live-area
      data-mw-path={path}
      data-mw-live={isLive ? "true" : "false"}
      data-mw-root-area={rootArea}
      style={{ height: "100%" }}
    >
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
      {view && connection ? (
        <EmbeddedAreaProvider
          factory={(address, ref) => {
            const src = new GrpcAreaSource(connection, address, {
              area: ref.area ?? "",
              id: ref.id as string | undefined,
              layout: ref.layout,
            });
            void src.start();
            return { source: src, rootArea: ref.area ?? "" };
          }}
        >
          {view}
        </EmbeddedAreaProvider>
      ) : (
        view ?? (!unauthenticated ? (
          <div data-mw-connecting style={{ padding: 48, textAlign: "center", opacity: 0.7 }}>
            Connecting to the mesh…
          </div>
        ) : null)
      )}
    </div>
  );
}
