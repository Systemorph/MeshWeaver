import type { CSSProperties, ReactNode } from "react";
import { useState } from "react";
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

function SplitterSkin({ control }: SkinProps): ReactNode {
  const children = useChildAreas(control);
  return (
    <div style={{ display: "flex", gap: 8, width: "100%" }}>
      {children.map((c, i) => (
        <div key={c.key || i} style={{ flex: 1, minWidth: 0 }}>
          <RenderArea areaKey={c.key} />
        </div>
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
