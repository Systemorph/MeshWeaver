import type { ReactNode } from "react";
import type { NamedArea, UiControl } from "../area/types.js";
import { ScopeProvider, useAreaState, useScope } from "../area/context.js";
import { useLeafPack } from "./registryContext.js";

export interface ControlProps {
  control: UiControl;
}

/**
 * Render one control. Skins are popped LIFO and wrap (or, for layout skins, lay out) the control;
 * a container with no remaining skin defaults to a stack; a leaf dispatches on `$type`. Components come
 * from the active leaf pack (see registryContext) — this core imports no concrete component.
 */
export function ControlRenderer({ control }: { control?: UiControl | null }): ReactNode {
  const pack = useLeafPack();
  if (control == null) return null;

  const skins = control.skins ?? [];
  if (skins.length > 0) {
    const skin = skins[skins.length - 1];
    const rest: UiControl = { ...control, skins: skins.slice(0, -1) };
    const Wrapper = pack.skins[skin.$type] ?? pack.skins.__default ?? pack.defaultContainer;
    return <Wrapper skin={skin} control={rest} />;
  }

  if (Array.isArray(control.areas)) {
    const Default = pack.defaultContainer;
    return <Default skin={{ $type: "LayoutStack" }} control={control} />;
  }

  // MeshWeaver serializes a control's `$type` as its full class name — `HtmlControl`, `MenuControl`,
  // `LayoutAreaControl` — while packs register leaves by the short name (`Html`, `Menu`, `LayoutArea`).
  // Dispatch on the exact `$type` first (a pack MAY register a full name), then on the suffix-stripped
  // name so the short-name convention resolves the real wire types.
  const Comp =
    pack.controls[control.$type] ?? pack.controls[stripControlSuffix(control.$type)] ?? pack.fallback;
  return <Comp control={control} />;
}

/** `HtmlControl` → `Html`; a `$type` that doesn't end in `Control` is returned unchanged. */
function stripControlSuffix(type: string): string {
  return type.endsWith("Control") ? type.slice(0, -"Control".length) : type;
}

export interface ChildArea {
  key: string;
  named: NamedArea;
  control?: UiControl;
}

/** Resolve a container's child areas (keys + the child controls they point at). */
export function useChildAreas(control: UiControl): ChildArea[] {
  const { area: parentArea } = useScope();
  const state = useAreaState();
  const areas = (control.areas as NamedArea[] | undefined) ?? [];
  return areas.map((named) => {
    const key = named.area || `${parentArea}/${named.id ?? ""}`;
    return { key, named, control: state.areas?.[key] };
  });
}

/** Render every child area of a container, each in its own area scope. */
export function RenderChildren({ control }: { control: UiControl }): ReactNode {
  const children = useChildAreas(control);
  return (
    <>
      {children.map((c, i) => (
        <RenderArea key={c.key || i} areaKey={c.key} />
      ))}
    </>
  );
}

/** Render a single area by key, scoping events + bindings to it. */
export function RenderArea({ areaKey }: { areaKey: string }): ReactNode {
  const { source } = useScope();
  const state = useAreaState();
  const control = state.areas?.[areaKey];
  return (
    <ScopeProvider source={source} area={areaKey} dataContext={control?.dataContext}>
      <ControlRenderer control={control} />
    </ScopeProvider>
  );
}
