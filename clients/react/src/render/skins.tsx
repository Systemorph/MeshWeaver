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
import { ControlRenderer, RenderArea, useChildAreas } from "./ControlRenderer.js";
import { cssAlign, cssSize } from "./style.js";

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
  return (
    <div style={style}>
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
    <div style={style}>
      <Children control={control} />
    </div>
  );
}

function PlainLayoutSkin({ control }: SkinProps): ReactNode {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
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
    <div style={{ display: "flex", flexDirection: "column", gap: 8, width: cssSize(skin.width) }}>
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
    <Toolbar>
      <Children control={control} />
    </Toolbar>
  );
}

/** The per-pane skin (SplitterPaneSkin) carried on a splitter child control. */
function paneSkinOf(control?: UiControl): Skin | undefined {
  return control?.skins?.find((s) => /splitterpane/i.test(String(s.$type)));
}

function paneWeight(control?: UiControl): number {
  const size = paneSkinOf(control)?.size;
  const n = size == null ? NaN : parseFloat(String(size));
  return Number.isFinite(n) && n > 0 ? n : 1;
}

/**
 * Interactive splitter — the React port of Blazor's FluentMultiSplitter (SplitterView): panes laid
 * out along the skin's orientation with DRAGGABLE gutters between them (was a static flex split).
 * Initial pane fractions come from each SplitterPaneSkin.Size (relative weights) or an equal split;
 * dragging a gutter reallocates fraction between its two adjacent panes, clamped to a 5% floor so a
 * pane never collapses to nothing. Pointer capture via window listeners keeps the drag smooth even
 * when the cursor leaves the thin gutter.
 */
function SplitterSkin({ skin, control }: SkinProps): ReactNode {
  const children = useChildAreas(control);
  const horizontal = String(skin.orientation ?? "Horizontal").toLowerCase() !== "vertical";
  const containerRef = useRef<HTMLDivElement>(null);

  const initial = useMemo(() => {
    const weights = children.map((c) => paneWeight(c.control));
    const total = weights.reduce((a, b) => a + b, 0) || 1;
    return weights.map((w) => w / total);
  }, [children]);

  const [sizes, setSizes] = useState<number[]>(initial);
  // Re-sync when the number of panes changes (a different splitter mounts under the same skin).
  useEffect(() => setSizes(initial), [initial.length]); // eslint-disable-line react-hooks/exhaustive-deps

  const startDrag = (index: number) => (e: ReactPointerEvent) => {
    const container = containerRef.current;
    if (!container || index + 1 >= children.length) return;
    e.preventDefault();
    const rect = container.getBoundingClientRect();
    const total = horizontal ? rect.width : rect.height;
    const origin = horizontal ? e.clientX : e.clientY;
    const startSizes = sizes.length === children.length ? [...sizes] : initial;
    const floor = 0.05;

    const move = (ev: PointerEvent) => {
      const cur = horizontal ? ev.clientX : ev.clientY;
      let delta = total > 0 ? (cur - origin) / total : 0;
      // Clamp so neither adjacent pane drops below the floor.
      delta = Math.max(delta, floor - startSizes[index]);
      delta = Math.min(delta, startSizes[index + 1] - floor);
      const next = [...startSizes];
      next[index] = startSizes[index] + delta;
      next[index + 1] = startSizes[index + 1] - delta;
      setSizes(next);
    };
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  };

  const gutter = horizontal ? { width: 5, cursor: "col-resize", alignSelf: "stretch" as const } : { height: 5, cursor: "row-resize" };

  return (
    <div
      ref={containerRef}
      style={{
        display: "flex",
        flexDirection: horizontal ? "row" : "column",
        width: cssSize(skin.width) ?? "100%",
        height: cssSize(skin.height) ?? (horizontal ? undefined : "100%"),
        minHeight: 0,
      }}
    >
      {children.map((c, i) => (
        <Fragment key={c.key || i}>
          <div style={{ flex: `0 0 ${(sizes[i] ?? 1 / children.length) * 100}%`, minWidth: 0, minHeight: 0, overflow: "auto" }}>
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
      ))}
    </div>
  );
}

function NavMenuSkin({ skin, control }: SkinProps): ReactNode {
  return (
    <nav
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
  return (
    <div>
      <div
        onClick={() => setOpen((o) => !o)}
        style={{ display: "flex", alignItems: "center", gap: 4, cursor: "pointer", padding: "4px 0" }}
      >
        {open ? <ChevronDown20Regular /> : <ChevronRight20Regular />}
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
    <Card style={{ padding: 12 }}>
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
    <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
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
    <form onSubmit={(e) => e.preventDefault()} style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <Children control={control} />
    </form>
  );
}

function semanticWrapper(tag: keyof JSX.IntrinsicElements): SkinComponent {
  return ({ control }) => {
    const Tag = tag as any;
    return (
      <Tag>
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
