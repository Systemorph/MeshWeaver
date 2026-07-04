import type { ReactNode } from "react";
import { useEffect, useState } from "react";
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Link,
  MessageBar,
  MessageBarBody,
  Spinner,
} from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { ScopeProvider, useAreaState, useEmit, useResolve, useScope } from "../area/context.js";
import { useMeshLink } from "../area/navigation.js";
import { RenderArea } from "../render/ControlRenderer.js";
import { useAreaSourceFactory, type EmbeddedAreaHandle } from "../render/embeddedArea.js";
import { str, useText } from "./common.js";

/** A leaf that renders a referenced area by key/path. */
function NamedAreaView({ control }: { control: UiControl }): ReactNode {
  const area = useText(control.area);
  return area ? <RenderArea areaKey={area} /> : null;
}

// ---- LayoutArea (nested area embed) ---------------------------------------------------------------
// LayoutAreaControl wire (src/MeshWeaver.Layout/LayoutAreaControl.cs):
//   { address, reference: { area, id?, layout? }, showProgress (default true), progressMessage,
//     spinnerType: "Ring"|"Dots"|"Skeleton"|"None" }
// The REAL embed opens a second AreaSource for the referenced (address, area, id) via the host's
// AreaSourceFactory (render/embeddedArea.tsx) and renders the nested tree in its own scope — the
// React mirror of Blazor's LayoutAreaView binding a second synchronization stream. Hosts without a
// factory (static demos, unit tests) get the link marker instead.

/** Renders inside the NESTED source's scope: spinner until the root area arrives, then the tree. */
function EmbeddedAreaBody({ rootArea, showProgress, progressMessage }: { rootArea: string; showProgress: boolean; progressMessage?: string }): ReactNode {
  const state = useAreaState();
  const loaded = state.areas?.[rootArea] != null;
  if (!loaded) return showProgress ? <Spinner size="small" label={progressMessage || undefined} /> : null;
  return <RenderArea areaKey={rootArea} />;
}

function LayoutAreaView({ control }: { control: UiControl }): ReactNode {
  const factory = useAreaSourceFactory();
  const rawAddress = useResolve(control.address);
  const address = typeof rawAddress === "string" ? rawAddress : str(rawAddress?.address ?? rawAddress?.path ?? "");
  const ref = (control.reference ?? {}) as Json;
  const area = str(ref.area ?? ref.Area ?? "");
  const id = ref.id ?? ref.Id;
  const layout = ref.layout ?? ref.Layout;
  const showProgress = useResolve(control.showProgress) !== false;
  const progressMessage = str(useResolve(control.progressMessage));
  const spinnerType = str(control.spinnerType);
  const key = `${address}|${area}|${id == null ? "" : str(id)}|${str(layout)}`;

  const [handle, setHandle] = useState<EmbeddedAreaHandle | null>(null);
  useEffect(() => {
    if (!factory || !address) return;
    const h = factory(address, { area: area || undefined, id, layout: layout ? String(layout) : undefined });
    setHandle(h);
    return () => {
      h?.dispose?.();
      setHandle(null);
    };
    // key captures address/area/id/layout; factory identity changes re-open the subscription.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [factory, key]);

  if (!factory || !address) {
    // No nested-area support in this host — the informative marker (previous behavior).
    return (
      <MessageBar intent="info">
        <MessageBarBody>
          Embedded layout area <b>{area}</b> @ {address}
        </MessageBarBody>
      </MessageBar>
    );
  }
  if (!handle) return showProgress && spinnerType !== "None" ? <Spinner size="small" label={progressMessage || undefined} /> : null;

  const rootArea = handle.rootArea ?? area;
  return (
    <ScopeProvider source={handle.source} area={rootArea}>
      <EmbeddedAreaBody rootArea={rootArea} showProgress={showProgress && spinnerType !== "None"} progressMessage={progressMessage} />
    </ScopeProvider>
  );
}

/** Blazor's DialogView size → --dialog-width mapping (S/M/L). */
function dialogWidth(size: string): string {
  switch (size) {
    case "S":
      return "400px";
    case "L":
      return "800px";
    default:
      return "600px";
  }
}

/**
 * A real modal dialog — the mirror of Blazor's DialogView (FluentDialog): shown on mount, title in
 * the header, ContentArea in the body, ActionsArea in the footer when HasActions (else a Close
 * button when IsClosable), and a CloseDialogEvent posted back to the owning hub on dismissal.
 */
function DialogView({ control }: { control: UiControl }): ReactNode {
  const [open, setOpen] = useState(true);
  const emit = useEmit();
  const { area } = useScope();
  const title = useText(control.title);
  const size = str(useResolve(control.size)) || "M";
  const isClosable = !!useResolve(control.isClosable);
  const hasActions = !!useResolve(control.hasActions);
  const contentArea = (control.contentArea as UiControl | undefined)?.area;
  const actionsArea = (control.actionsArea as UiControl | undefined)?.area;
  const close = (state: "OK" | "Cancel") => {
    setOpen(false);
    emit({ kind: "closeDialog", area, value: state });
  };
  return (
    <Dialog open={open} modalType="modal" onOpenChange={(_, d) => !d.open && close("Cancel")}>
      <DialogSurface style={{ maxWidth: dialogWidth(size) }}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>{contentArea ? <RenderArea areaKey={String(contentArea)} /> : null}</DialogContent>
          {hasActions && actionsArea ? (
            <DialogActions>
              <RenderArea areaKey={String(actionsArea)} />
            </DialogActions>
          ) : isClosable ? (
            <DialogActions>
              <Button appearance="secondary" onClick={() => close("OK")}>
                Close
              </Button>
            </DialogActions>
          ) : null}
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function RedirectView({ control }: { control: UiControl }): ReactNode {
  const href = useText(control.href);
  const link = useMeshLink(href || undefined);
  return (
    <Link href={link.href} onClick={link.onClick}>
      {href}
    </Link>
  );
}

export const containerControls = {
  NamedArea: NamedAreaView,
  LayoutArea: LayoutAreaView,
  Dialog: DialogView,
  Redirect: RedirectView,
};
