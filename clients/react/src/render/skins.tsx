import type { CSSProperties, PointerEvent as ReactPointerEvent, ReactNode } from "react";
import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import {
  Card,
  Divider,
  Tab,
  TabList,
  Toolbar,
  Text,
  Subtitle2,
  type SelectTabData,
  type SelectTabEvent,
} from "@fluentui/react-components";
import { ChevronDown20Regular, ChevronRight20Regular } from "@fluentui/react-icons";
import type { Skin, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { resolveIconByName } from "../controls/icon.js";
import { ControlRenderer, RenderArea, useChildAreas } from "./ControlRenderer.js";
import { controlClass, controlStyle, cssAlign, cssSize } from "./style.js";

export interface SkinProps {
  skin: Skin;
  control: UiControl;
}

type SkinComponent = (props: SkinProps) => ReactNode;

/** Vertical/horizontal flex stack — the default container layout (LayoutStackSkin). */
export function DefaultStackSkin({ skin, control }: SkinProps): ReactNode {
  const horizontal = String(skin.orientation ?? "Vertical").toLowerCase() === "horizontal";
  const style: CSSProperties = {
    display: "flex",
    flexDirection: horizontal ? "row" : "column",
    gap: (horizontal ? skin.horizontalGap : skin.verticalGap) ?? skin.gap ?? 8,
    flexWrap: skin.wrap ? "wrap" : "nowrap",
    alignItems: cssAlign(horizontal ? skin.verticalAlignment : skin.horizontalAlignment),
    justifyContent: cssAlign(horizontal ? skin.horizontalAlignment : skin.verticalAlignment),
    width: cssSize(skin.width),
    height: cssSize(skin.height),
    boxSizing: "border-box",
  };
  // The control's own inline style (WithStyle) wins over the skin defaults — e.g. an overlay stack
  // declared `position: absolute; top; right` (the pinned-card unpin toggle) must escape flow to sit
  // ON the card, not render as a full-width bar below it. Blazor honours the Style the same way.
  return (
    <div className={controlClass(control)} style={{ ...style, ...controlStyle(control) }}>
      <Children control={control} />
    </div>
  );
}

// Renders a container's child areas (each in its own scope) — the shared body of every layout skin.
function Children({ control }: { control: UiControl }): ReactNode {
  const children = useChildAreas(control);
  return (
    <>
      {children.map((c, i) => (
        <RenderArea key={c.key || i} areaKey={c.key} />
      ))}
    </>
  );
}

function GridSkin({ skin, control }: SkinProps): ReactNode {
  const style: CSSProperties = {
    display: "grid",
    gap: skin.spacing ?? 8,
    gridTemplateColumns: skin.gridTemplateColumns ?? "repeat(12, minmax(0, 1fr))",
    width: "100%",
  };
  return (
    <div className={controlClass(control)} style={style}>
      <Children control={control} />
    </div>
  );
}

function PlainLayoutSkin({ control }: SkinProps): ReactNode {
  return (
    <div className={controlClass(control)} style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <Children control={control} />
    </div>
  );
}

function TabsSkin({ skin, control }: SkinProps): ReactNode {
  const children = useChildAreas(control);
  const tabs = children.map((c, i) => ({
    value: String(c.named.id ?? i),
    key: c.key,
    label: tabLabel(c.control) ?? String(c.named.id ?? `Tab ${i + 1}`),
  }));
  const [selected, setSelected] = useState<string>(String(skin.activeTabId ?? tabs[0]?.value ?? "0"));
  const active = tabs.find((t) => t.value === selected) ?? tabs[0];
  return (
    <div className={controlClass(control)} style={{ display: "flex", flexDirection: "column", gap: 8, width: cssSize(skin.width) }}>
      <TabList selectedValue={selected} onTabSelect={(_: SelectTabEvent, d: SelectTabData) => setSelected(String(d.value))}>
        {tabs.map((t) => (
          <Tab key={t.value} value={t.value}>
            {t.label}
          </Tab>
        ))}
      </TabList>
      {active ? <RenderArea areaKey={active.key} /> : null}
    </div>
  );
}

function tabLabel(control?: UiControl): string | undefined {
  const skin = control?.skins?.find((s) => /tab/i.test(s.$type));
  return skin?.label != null ? String(skin.label) : undefined;
}

function ToolbarSkin({ control }: SkinProps): ReactNode {
  return (
    <Toolbar className={controlClass(control)}>
      <Children control={control} />
    </Toolbar>
  );
}

/** The per-pane skin (SplitterPaneSkin) carried on a splitter child control. */
function paneSkinOf(control?: UiControl): Skin | undefined {
  return control?.skins?.find((s) => /splitterpane/i.test(String(s.$type)));
}

/** Parse a pixel size ("280px", "280", 280) to a number; `null` for star / percentage / unset. */
function parsePx(v: unknown): number | null {
  if (v == null) return null;
  if (typeof v === "number") return Number.isFinite(v) ? v : null;
  const s = String(v).trim();
  const m = /^(-?\d*\.?\d+)\s*px$/i.exec(s) ?? /^(-?\d*\.?\d+)$/.exec(s);
  return m ? parseFloat(m[1]) : null;
}

/**
 * A pane's sizing spec, mirroring SplitterPaneSkin.Size semantics: a definite PIXEL width
 * ("280px" → `fixedPx`) or a STAR weight ("*", "2*", or unspecified → `grow`, filling the
 * remainder). This is the fix for the old `parseFloat("280px")=280` vs `parseFloat("*")=1`
 * bug that made a 280px pane a 280:1 STAR weight (≈full width) instead of a fixed 280px.
 */
interface PaneSpec {
  fixedPx: number | null;
  grow: number;
  minPx: number | null;
  maxPx: number | null;
}

function paneSpec(control?: UiControl): PaneSpec {
  const skin = paneSkinOf(control);
  const minPx = parsePx(skin?.min);
  const maxPx = parsePx(skin?.max);
  const size = skin?.size;
  const s = size == null ? "" : String(size).trim();
  const star = /^(\d*\.?\d*)\*$/.exec(s); // "*", "2*", "1.5*"
  if (s === "" || s === "*" || star) {
    const w = star && star[1] ? parseFloat(star[1]) : 1;
    return { fixedPx: null, grow: w > 0 ? w : 1, minPx, maxPx };
  }
  const px = parsePx(size);
  if (px != null) return { fixedPx: px, grow: 0, minPx, maxPx };
  // Anything else (e.g. a "50%") → treat as a star pane so it still fills.
  return { fixedPx: null, grow: 1, minPx, maxPx };
}

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(Math.max(v, lo), hi);
}

/**
 * Interactive splitter — the React port of Blazor's FluentMultiSplitter (SplitterView): panes laid
 * out along the skin's orientation with DRAGGABLE gutters between them. Each pane sizes per its
 * SplitterPaneSkin.Size: a PIXEL pane ("280px") gets a fixed flex-basis (`flex: 0 0 280px`, no grow)
 * while a STAR pane ("*"/unspecified) fills the remaining space (`flex: <weight> 1 0`) — so a fixed
 * 280px menu stays 280px and the content pane takes the rest, matching Blazor. Dragging a gutter
 * resizes the adjacent FIXED pane (in px, clamped to its Min/Max), leaving the star neighbour to
 * re-fill. Pointer capture via window listeners keeps the drag smooth even off the thin gutter.
 */
function SplitterSkin({ skin, control }: SkinProps): ReactNode {
  const children = useChildAreas(control);
  const horizontal = String(skin.orientation ?? "Horizontal").toLowerCase() !== "vertical";
  const containerRef = useRef<HTMLDivElement>(null);

  const specs = useMemo(() => children.map((c) => paneSpec(c.control)), [children]);

  // Per-pane pixel OVERRIDE set by dragging (null = use the spec). Reset when the pane shape changes,
  // keyed on the id/size signature so a different splitter with the same count doesn't inherit stale
  // overrides.
  const shapeKey = useMemo(
    () =>
      children
        .map((c, i) => `${String(c.control?.id ?? c.key ?? i)}:${specs[i].fixedPx ?? `*${specs[i].grow}`}`)
        .join("|"),
    [children, specs],
  );
  const [overrides, setOverrides] = useState<(number | null)[]>(() => children.map(() => null));
  useEffect(() => setOverrides(children.map(() => null)), [shapeKey]); // eslint-disable-line react-hooks/exhaustive-deps

  const effectiveFixed = (i: number): number | null => overrides[i] ?? specs[i].fixedPx;

  const startDrag = (index: number) => (e: ReactPointerEvent) => {
    const container = containerRef.current;
    if (!container || index + 1 >= children.length) return;
    e.preventDefault();
    const panes = container.querySelectorAll<HTMLElement>(":scope > [data-splitter-pane]");
    const measure = (el?: HTMLElement) => (el ? (horizontal ? el.getBoundingClientRect().width : el.getBoundingClientRect().height) : 0);
    const startA = measure(panes[index]);
    const startB = measure(panes[index + 1]);
    const origin = horizontal ? e.clientX : e.clientY;
    const specA = specs[index];
    const specB = specs[index + 1];
    // Resize the FIXED side (so the star neighbour keeps filling). If neither is fixed, resize the
    // left pane (converting it to a pixel override); if both are fixed, also resize the left.
    const resizeLeft = effectiveFixed(index) != null || effectiveFixed(index + 1) == null;
    const floor = 40; // keep a pane usably wide when it has no explicit Min

    const move = (ev: PointerEvent) => {
      const cur = horizontal ? ev.clientX : ev.clientY;
      const delta = cur - origin;
      setOverrides((prev) => {
        const next = [...prev];
        if (resizeLeft) {
          const lo = specA.minPx ?? floor;
          const hi = specA.maxPx ?? Math.max(lo, startA + startB - (specB.minPx ?? floor));
          next[index] = clamp(startA + delta, lo, hi);
        } else {
          const lo = specB.minPx ?? floor;
          const hi = specB.maxPx ?? Math.max(lo, startA + startB - (specA.minPx ?? floor));
          next[index + 1] = clamp(startB - delta, lo, hi);
        }
        return next;
      });
    };
    // Tear the drag down on ANY terminal — pointerup, pointercancel (touch interrupted), or the
    // window losing focus mid-drag. Without pointercancel/blur the window listeners leaked and kept
    // resizing on later pointer moves.
    const end = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", end);
      window.removeEventListener("blur", end);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", end);
    window.addEventListener("blur", end);
  };

  const gutter = horizontal ? { width: 5, cursor: "col-resize", alignSelf: "stretch" as const } : { height: 5, cursor: "row-resize" };

  return (
    <div
      ref={containerRef}
      className={controlClass(control)}
      style={{
        display: "flex",
        flexDirection: horizontal ? "row" : "column",
        width: cssSize(skin.width) ?? "100%",
        height: cssSize(skin.height) ?? (horizontal ? undefined : "100%"),
        minHeight: 0,
      }}
    >
      {children.map((c, i) => {
        const fixedPx = effectiveFixed(i);
        const flex = fixedPx != null ? `0 0 ${fixedPx}px` : `${specs[i].grow} 1 0%`;
        return (
          <Fragment key={c.key || i}>
            <div data-splitter-pane="" style={{ flex, minWidth: 0, minHeight: 0, overflow: "auto" }}>
              <RenderArea areaKey={c.key} />
            </div>
            {i < children.length - 1 ? (
              <div
                role="separator"
                aria-orientation={horizontal ? "vertical" : "horizontal"}
                onPointerDown={startDrag(i)}
                style={{ flex: "0 0 auto", background: "var(--colorNeutralStroke2)", touchAction: "none", ...gutter }}
              />
            ) : null}
          </Fragment>
        );
      })}
    </div>
  );
}

function NavMenuSkin({ skin, control }: SkinProps): ReactNode {
  return (
    <nav
      className={controlClass(control)}
      style={{
        display: "flex",
        flexDirection: "column",
        gap: 2,
        width: cssSize(skin.width) ?? 250,
        padding: 8,
        borderRight: "1px solid var(--colorNeutralStroke2)",
      }}
    >
      <Children control={control} />
    </nav>
  );
}

function NavGroupSkin({ skin, control }: SkinProps): ReactNode {
  const [open, setOpen] = useState<boolean>(skin.expanded !== false);
  const GroupIcon = resolveIconByName(skin.icon);
  return (
    <div className={controlClass(control)}>
      <div
        onClick={() => setOpen((o) => !o)}
        style={{ display: "flex", alignItems: "center", gap: 4, cursor: "pointer", padding: "4px 0" }}
      >
        {open ? <ChevronDown20Regular /> : <ChevronRight20Regular />}
        {GroupIcon ? <GroupIcon /> : null}
        <Subtitle2>{String(skin.title ?? "")}</Subtitle2>
      </div>
      {open ? (
        <div style={{ paddingLeft: 16, display: "flex", flexDirection: "column", gap: 2 }}>
          <Children control={control} />
        </div>
      ) : null}
    </div>
  );
}

function CardSkin({ control }: SkinProps): ReactNode {
  return (
    <Card className={controlClass(control)} style={{ padding: 12 }}>
      <ControlRenderer control={control} />
    </Card>
  );
}

/**
 * PropertySkin — the per-field wrapper of an EditForm, mirroring Blazor's PropertyView
 * (src/MeshWeaver.Blazor/Components/PropertyView.razor): a label (Skin.Label, falling back to
 * Name/title), an optional description line, then the bound field control.
 */
function PropertySkin({ skin, control }: SkinProps): ReactNode {
  const label = useResolve(skin.label ?? skin.name ?? skin.title);
  const description = useResolve(skin.description);
  return (
    <div className={controlClass(control)} style={{ display: "flex", flexDirection: "column", gap: 2 }}>
      {label != null && String(label).length > 0 ? (
        <Text weight="semibold" as="span">
          <label htmlFor={skin.name != null ? String(skin.name) : undefined}>{String(label)}</label>
        </Text>
      ) : null}
      {description != null && String(description).length > 0 ? (
        <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
          {String(description)}
        </Text>
      ) : null}
      <ControlRenderer control={control} />
    </div>
  );
}

/**
 * EditFormSkin — the form wrapper, mirroring Blazor's EditFormView: the child property areas
 * stacked as a form (each wrapped in PropertySkin). Fields data-bind per-edit through the standard
 * update event — the owning hub persists every change, so no explicit submit button is needed.
 */
function EditFormSkin({ control }: SkinProps): ReactNode {
  return (
    <form onSubmit={(e) => e.preventDefault()} className={controlClass(control)} style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <Children control={control} />
    </form>
  );
}

function semanticWrapper(tag: keyof JSX.IntrinsicElements): SkinComponent {
  return ({ control }) => {
    const Tag = tag as any;
    return (
      <Tag className={controlClass(control)}>
        <ControlRenderer control={control} />
      </Tag>
    );
  };
}

function passthrough({ control }: SkinProps): ReactNode {
  return <ControlRenderer control={control} />;
}

export const skinRegistry: Record<string, SkinComponent> = {
  LayoutStack: DefaultStackSkin,
  Layout: PlainLayoutSkin,
  LayoutGrid: GridSkin,
  LayoutGridItem: passthrough,
  Tabs: TabsSkin,
  Tab: passthrough,
  Toolbar: ToolbarSkin,
  Splitter: SplitterSkin,
  NavMenu: NavMenuSkin,
  NavGroup: NavGroupSkin,
  Card: CardSkin,
  Property: PropertySkin,
  EditForm: EditFormSkin,
  Editor: PlainLayoutSkin,
  Main: semanticWrapper("main"),
  Header: semanticWrapper("header"),
  Footer: semanticWrapper("footer"),
  BodyContent: semanticWrapper("section"),
  __default: passthrough,
};

export { Divider };
