// ItemTemplate — the repeater. Blazor (Components/ItemTemplate.razor) renders the `view` template
// once per item of the bound `data` collection, giving each instance
// DataContext = "{dataPointer}/{i}" so the template's RELATIVE bindings resolve (and write back)
// against that item. The React mirror does exactly that with a per-item ScopeProvider dataContext —
// the core's pointer resolver already composes relative pointers against the scope's dataContext.

import type { CSSProperties, ReactNode } from "react";
import { ScopeProvider, useBindingPointer, useResolve, useScope } from "../area/context.js";
import type { UiControl } from "../area/types.js";
import { ControlRenderer } from "../render/ControlRenderer.js";

/** MeshWeaver.Layout.Orientation: Horizontal = 0, Vertical = 1 (serialized as number or name). */
function isHorizontal(orientation: unknown): boolean {
  if (orientation == null) return false; // Blazor default: vertical
  if (typeof orientation === "number") return orientation === 0;
  return String(orientation).toLowerCase() === "horizontal";
}

function ItemTemplateView({ control }: { control: UiControl }): ReactNode {
  const { source, area } = useScope();
  const items = useResolve(control.data);
  // Absolute pointer of the bound collection (undefined for a literal array — then the template
  // renders without per-item scope, matching Blazor which requires a bound Data for item binding).
  const basePointer = useBindingPointer(control.data);
  const horizontal = isHorizontal(useResolve(control.orientation));
  const wrap = !!useResolve(control.wrap);
  const view = control.view as UiControl | undefined;
  const arr = Array.isArray(items) ? items : [];
  if (!view) return null;
  const style: CSSProperties = {
    display: "flex",
    flexDirection: horizontal ? "row" : "column",
    flexWrap: wrap ? "wrap" : "nowrap",
    gap: 8,
  };
  return (
    <div style={style}>
      {arr.map((_, i) => (
        <ScopeProvider key={i} source={source} area={area} dataContext={basePointer ? `${basePointer}/${i}` : undefined}>
          <ControlRenderer control={view} />
        </ScopeProvider>
      ))}
    </div>
  );
}

export const itemTemplateControls = {
  ItemTemplate: ItemTemplateView,
};
