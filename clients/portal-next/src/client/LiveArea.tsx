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

import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { useRouter } from "next/navigation";
import { Button, MessageBar, MessageBarActions, MessageBarBody, Spinner } from "@fluentui/react-components";
import { classifyAreaError, EmbeddedAreaProvider, MeshAreaView, StaticAreaSource } from "@meshweaver/react";
import type { AreaSource, AreaTree } from "@meshweaver/react";
import { deniedRedirectTarget, rootAreaOf, targetKey, type AreaTarget } from "./live";
import { useLiveConnection, usePublishNavigation } from "./LiveConnection";
import { useHydratedTheme } from "./useHydratedTheme";

/** Redirect an unauthenticated visitor to the portal's unified /login, preserving where they were so
 *  sign-in returns them here (the Blazor NavigationService appends the same returnUrl). /login is a
 *  root-origin portal page (not a basePath route), so a full-page window.location navigation is right. */
function goToLogin(): void {
  if (typeof window === "undefined") return;
  const returnUrl = encodeURIComponent(window.location.href);
  window.location.href = `/login?returnUrl=${returnUrl}`;
}

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
  /** True when the server-side render detected an RLS access denial (the authenticated visitor lacks
   *  Read). Combined with a safely-configured `target.redirectOnDenied`, the client redirects to the
   *  node's public cover / paywall — the same "no access ⇒ redirect here" the Blazor NamedAreaView does. */
  initialDenied?: boolean;
}

/** True once a source has folded its first Full frame (an empty tree is the pre-frame state). */
function hasContent(source: AreaSource): boolean {
  const state = source.getState();
  return state.areas != null && Object.keys(state.areas).length > 0;
}

const noSubscription = () => () => {};

export function LiveArea({ path, target, initialTree, initialRootArea = "", unauthenticated, initialDenied }: LiveAreaProps) {
  const { theme } = useHydratedTheme();
  const router = useRouter();
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

  // Classify a live-subscription fault so the shell makes the SAME decisions the Blazor NamedAreaView
  // makes: an access denial can redirect to a paywall; a routing NotFound gets a graceful placeholder
  // (never the raw "No node found at 'u/_Activity/…'" diagnostic); anything else shows verbatim.
  const errorInfo = streamError ? classifyAreaError(streamError) : null;

  // "No access ⇒ redirect here": a denied visitor — detected on the server render (initialDenied) or
  // by a live access-denied fault mid-session — is sent to the node's public cover / paywall when its
  // policy safely configures one (deniedRedirectTarget's loop-guard refuses a target that would bounce
  // back). Without a safe target it falls through to the honest denied affordance below. The paywall
  // is an in-app mesh route → navigate via the basePath-aware app router.
  const redirectTarget = deniedRedirectTarget({
    initialDenied,
    streamError,
    address: target.address,
    redirectOnDenied: target.redirectOnDenied,
  });
  useEffect(() => {
    if (redirectTarget) router.replace(redirectTarget);
  }, [redirectTarget, router]);

  // ONE hub: the embed factory comes from the session's single MeshAreaRegistry (LiveConnection),
  // the SAME registry getAreaSource uses — a page area and any `@@` embed of it share one stream.
  // (Previously each LiveArea built its own factory over the connection: a second registry, a second
  // subscription per embed, and — when churned — the non-deterministic "different sections" refresh.)
  const embedFactory = live.embeddedFactory;

  // The live source roots at the explicit area name when the URL addressed one; the SSR seed
  // roots wherever the server said its tree roots (the rendered frame's area, or "" for the
  // synthesized preview).
  const rootArea = isLive ? rootAreaOf(target) : initialRootArea;

  // Not signed in AND no snapshot to show → the page is useless; send them to the portal's unified
  // /login page (the "proper redirect to login"). A page WITH SSR content (public) is left in place
  // with a sign-in affordance rather than yanked away from an anonymous reader.
  const needsLogin = !!unauthenticated && !active;
  useEffect(() => {
    if (needsLogin) goToLogin();
  }, [needsLogin]);

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
      {offline && (() => {
        const reason = (live.state as { reason: string }).reason;
        // A 401/auth reason is really "not signed in" — offer the login redirect, not just a notice.
        const authFailed = /\b401\b|unauth|token mint/i.test(reason);
        return (
          <div data-mw-offline style={{ marginBottom: 16 }}>
            <MessageBar intent="warning">
              <MessageBarBody>
                No live mesh connection on this origin ({reason}). Showing the server-rendered snapshot only.
              </MessageBarBody>
              {authFailed && (
                <MessageBarActions>
                  <Button size="small" appearance="primary" onClick={goToLogin}>
                    Sign in
                  </Button>
                </MessageBarActions>
              )}
            </MessageBar>
          </div>
        );
      })()}
      {streamError && !redirectTarget && (
        <div data-mw-area-error data-mw-error-kind={errorInfo?.kind ?? "other"} style={{ marginBottom: 16 }}>
          <MessageBar intent={errorInfo?.kind === "access-denied" ? "warning" : errorInfo?.kind === "not-found" ? "info" : "error"}>
            <MessageBarBody>
              {errorInfo?.kind === "access-denied"
                ? `You don’t have access to “${path}”.`
                : errorInfo?.kind === "not-found"
                  ? `“${path}” is no longer available.`
                  : `Live area “${path}” failed: ${streamError}`}
            </MessageBarBody>
          </MessageBar>
        </div>
      )}
      {unauthenticated && !active && (
        <MessageBar intent="info">
          <MessageBarBody>Not signed in — taking you to sign in…</MessageBarBody>
          <MessageBarActions>
            <Button size="small" appearance="primary" onClick={() => (window.location.href = "/login")}>
              Sign in
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {view && embedFactory ? (
        <EmbeddedAreaProvider factory={embedFactory}>{view}</EmbeddedAreaProvider>
      ) : (
        view ?? (!unauthenticated ? <ConnectingState path={path} connecting={live.state.kind === "connecting"} /> : null)
      )}
    </div>
  );
}

/**
 * The no-content pending state — a spinner that ESCALATES after a bounded wait instead of showing
 * "Connecting to the mesh…" forever (the audit found /settings pending indefinitely with no signal).
 * Stream faults still surface through the areaErrors MessageBar the moment they arrive; this covers
 * the silent case: no SSR snapshot AND no live frame.
 */
function ConnectingState({ path, connecting }: { path: string; connecting: boolean }) {
  const [stalled, setStalled] = useState(false);
  useEffect(() => {
    const t = setTimeout(() => setStalled(true), 10_000);
    return () => clearTimeout(t);
  }, []);
  if (!stalled)
    return (
      <div data-mw-connecting style={{ padding: 48, display: "flex", justifyContent: "center" }}>
        <Spinner size="small" label={connecting ? "Connecting to the mesh…" : `Loading “${path || "home"}”…`} />
      </div>
    );
  return (
    <div data-mw-connecting-stalled style={{ padding: 24 }}>
      <MessageBar intent="warning">
        <MessageBarBody>
          {connecting
            ? "Still connecting to the mesh — the live connection has not come up."
            : `“${path || "home"}” has not delivered any content — the path may not resolve to a node you can read.`}
        </MessageBarBody>
        <MessageBarActions>
          <Button size="small" onClick={() => window.location.reload()}>
            Reload
          </Button>
        </MessageBarActions>
      </MessageBar>
    </div>
  );
}
