import type { ReactNode } from "react";
import { Card, Divider, Link, MessageBar, MessageBarBody, Text, Title3 } from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { RenderArea } from "../render/ControlRenderer.js";
import { str, useText } from "./common.js";

/** A leaf that renders a referenced area by key/path. */
function NamedAreaView({ control }: { control: UiControl }): ReactNode {
  const area = useText(control.area);
  return area ? <RenderArea areaKey={area} /> : null;
}

/** Embeds a layout area from another address — needs a second stream; shown as a marker for now. */
function LayoutAreaView({ control }: { control: UiControl }): ReactNode {
  const ref = control.reference as Json;
  return (
    <MessageBar intent="info">
      <MessageBarBody>
        Embedded layout area <b>{str(ref?.area ?? ref?.Area)}</b> @ {useText(control.address)}
      </MessageBarBody>
    </MessageBar>
  );
}

function DialogView({ control }: { control: UiControl }): ReactNode {
  const title = useText(control.title);
  const contentArea = (control.contentArea as UiControl | undefined)?.area;
  const actionsArea = (control.actionsArea as UiControl | undefined)?.area;
  return (
    <Card style={{ padding: 16, maxWidth: 520, boxShadow: "var(--shadow16)" }}>
      {title ? <Title3>{title}</Title3> : null}
      {contentArea ? <RenderArea areaKey={String(contentArea)} /> : null}
      {actionsArea ? (
        <>
          <Divider />
          <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
            <RenderArea areaKey={String(actionsArea)} />
          </div>
        </>
      ) : null}
    </Card>
  );
}

function RedirectView({ control }: { control: UiControl }): ReactNode {
  const href = useText(control.href);
  return <Link href={href}>{href}</Link>;
}

interface Series {
  name?: string;
  data?: number[];
  values?: number[];
}

function ChartView({ control }: { control: UiControl }): ReactNode {
  const title = useText(control.title);
  const labels = (useResolve(control.labels) as Json[]) ?? [];
  const series = ((useResolve(control.series) as Series[]) ?? []).map((s) => ({
    name: str(s.name),
    data: (s.data ?? s.values ?? []).map(Number),
  }));
  const max = Math.max(1, ...series.flatMap((s) => s.data));
  const colors = ["#0f6cbd", "#107c10", "#d83b01", "#5c2e91", "#008272"];
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      {title ? <Text weight="semibold">{title}</Text> : null}
      <div style={{ display: "flex", alignItems: "flex-end", gap: 12, height: 160, padding: "8px 0" }}>
        {labels.map((lbl, i) => (
          <div key={i} style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 4, flex: 1 }}>
            <div style={{ display: "flex", alignItems: "flex-end", gap: 2, height: 120 }}>
              {series.map((s, si) => (
                <div
                  key={si}
                  title={`${s.name}: ${s.data[i] ?? 0}`}
                  style={{ width: 14, height: `${((s.data[i] ?? 0) / max) * 120}px`, background: colors[si % colors.length], borderRadius: 2 }}
                />
              ))}
            </div>
            <Text size={100}>{str(lbl)}</Text>
          </div>
        ))}
      </div>
      <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
        {series.map((s, si) => (
          <span key={si} style={{ display: "flex", alignItems: "center", gap: 4 }}>
            <span style={{ width: 10, height: 10, background: colors[si % colors.length], borderRadius: 2 }} />
            <Text size={200}>{s.name}</Text>
          </span>
        ))}
      </div>
    </div>
  );
}

export const containerControls = {
  NamedArea: NamedAreaView,
  LayoutArea: LayoutAreaView,
  Dialog: DialogView,
  Redirect: RedirectView,
  Chart: ChartView,
};
