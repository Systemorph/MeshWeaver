import type { ReactNode } from "react";
import type { NamedArea, UiControl } from "../area/types.js";
import { ScopeProvider, useAreaState, useScope } from "../area/context.js";
import { controlRegistry, FallbackControl } from "./registry.js";
import { skinRegistry, DefaultStackSkin } from "./skins.js";

export interface ControlProps {
  control: UiControl;
}

/**
 * Render one control. Skins are popped LIFO and wrap (or, for layout skins, lay out) the control;
 * a container with no remaining skin defaults to a vertical stack; a leaf dispatches on `$type`.
 */
export function ControlRenderer({ control }: { control?: UiControl | null }): ReactNode {
  if (control == null) return null;

  const skins = control.skins ?? [];
  if (skins.length > 0) {
    const skin = skins[skins.length - 1];
    const rest: UiControl = { ...control, skins: skins.slice(0, -1) };
    const Wrapper = skinRegistry[skin.$type] ?? skinRegistry.__default;
    return <Wrapper skin={skin} control={rest} />;
  }

  if (Array.isArray(control.areas)) {
    return <DefaultStackSkin skin={{ $type: "LayoutStack" }} control={control} />;
  }

  const Comp = controlRegistry[control.$type] ?? FallbackControl;
  return <Comp control={control} />;
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
