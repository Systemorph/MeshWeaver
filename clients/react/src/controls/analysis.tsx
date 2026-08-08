// The standard analysis views (#745) — KPI strip, excess-of-loss tower, paired comparison bars —
// for the WEB (Fluent) pack. The Blazor twins live in
// src/MeshWeaver.Blazor/Components/{KpiStrip,Tower,ComparisonBars}View.razor.
//
// The SHAPE they draw belongs to the framework, not to either renderer: `analysisLayout.ts` holds
// the ports of TowerControl.Layout / ComparisonBarsControl.Layout, renderer-free so the React Native
// pack computes from the same numbers. Everything here is projection.
//
// Drawn with proportionally sized DOM blocks rather than SVG, for the same reasons as the Blazor
// side: the design tokens theme them for free, they reflow when the container is narrow, and every
// label stays real selectable text.

import type { CSSProperties, ReactNode } from "react";
import { Text } from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshLink } from "../area/navigation.js";
import { useLocalize } from "../i18n/LocaleContext.js";
import { controlClass, controlStyle, mergeClass } from "../render/style.js";
import { str, useText } from "./common.js";
import { formatValue } from "./format.js";
import {
  analysisRows as rows,
  clamp,
  comparisonLayout,
  navigableHref,
  num,
  optionalNum,
  towerLayout,
  towerTicks,
  type ComparisonPairWire as ComparisonPair,
  type TowerBandPlacement,
  type TowerBandWire as TowerBand,
} from "./analysisLayout.js";

// Re-exported for back-compat with importers that reached for the geometry here.
export {
  comparisonLayout,
  navigableHref,
  towerLayout,
  type ComparisonPlacement,
  type ComparisonRow,
  type TowerBandPlacement,
  type TowerPlacement,
} from "./analysisLayout.js";

const hintStyle: CSSProperties = { color: "var(--colorNeutralForeground3)" };
const emptyStyle: CSSProperties = { ...hintStyle, fontStyle: "italic", margin: 0 };

// ---------------------------------------------------------------------------- KPI strip

interface KpiItem {
  label?: Json;
  value?: Json;
  hint?: Json;
}

function KpiStripView({ control }: { control: UiControl }): ReactNode {
  const t = useLocalize();
  const items = rows<KpiItem>(useResolve(control.items));
  const minWidth = str(useResolve(control.minTileWidth)) || "150px";

  if (items.length === 0)
    return (
      <Text italic size={200} style={emptyStyle}>
        {t("analysis.kpi.empty")}
      </Text>
    );

  return (
    <div
      className={mergeClass("mw-kpi-strip", controlClass(control))}
      style={{ display: "flex", flexWrap: "wrap", gap: 10, alignItems: "stretch", ...controlStyle(control) }}
    >
      {items.map((item, i) => (
        <div
          key={i}
          style={{
            flex: "1 1 auto",
            minWidth,
            border: "1px solid var(--colorNeutralStroke2)",
            borderRadius: 10,
            padding: "10px 14px",
          }}
        >
          <div style={{ fontSize: 11, letterSpacing: ".08em", textTransform: "uppercase", ...hintStyle }}>
            {str(item.label)}
          </div>
          <div style={{ fontSize: 20, fontWeight: 700, marginTop: 2, color: "var(--colorNeutralForeground1)" }}>
            {str(item.value)}
          </div>
          {item.hint ? <div style={{ fontSize: 11, marginTop: 2, ...hintStyle }}>{str(item.hint)}</div> : null}
        </div>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------- tower


/** One band. Its own component so a linked band can take the host's routing hook. */
function TowerBandBox({ placement }: { placement: TowerBandPlacement }): ReactNode {
  const href = navigableHref(str(placement.band.href));
  const link = useMeshLink(href ?? undefined);
  const boxStyle: CSSProperties = {
    position: "absolute",
    left: 0,
    right: 0,
    bottom: `${placement.bottomPercent}%`,
    height: `${placement.heightPercent}%`,
    display: "block",
    overflow: "hidden",
    border: "1px solid var(--colorNeutralStroke2)",
    backgroundColor: "var(--colorBrandBackground2)",
    textDecoration: "none",
  };
  const body = (
    <>
      <span
        style={{
          position: "absolute",
          top: 0,
          bottom: 0,
          left: 0,
          width: `${clamp(num(placement.band.share), 0, 1) * 100}%`,
          backgroundColor: "var(--colorBrandBackground)",
        }}
      />
      <span style={{ position: "relative", display: "block", padding: "4px 10px", minWidth: 0 }}>
        <span
          style={{
            display: "block",
            fontSize: 13,
            fontWeight: 600,
            color: "var(--colorNeutralForeground1)",
            whiteSpace: "nowrap",
            overflow: "hidden",
            textOverflow: "ellipsis",
          }}
        >
          {str(placement.band.label)}
        </span>
        <span
          style={{
            display: "block",
            fontSize: 11,
            whiteSpace: "nowrap",
            overflow: "hidden",
            textOverflow: "ellipsis",
            ...hintStyle,
          }}
        >
          {str(placement.band.terms)}
        </span>
      </span>
    </>
  );
  return href ? (
    <a style={boxStyle} {...link}>
      {body}
    </a>
  ) : (
    <div style={boxStyle}>{body}</div>
  );
}

function TowerView({ control }: { control: UiControl }): ReactNode {
  const t = useLocalize();
  const layout = towerLayout(rows<TowerBand>(useResolve(control.bands)));
  const currency = useText(control.currency);
  const retentionLabel = useText(control.retentionLabel) || t("analysis.tower.retained");
  const height = str(useResolve(control.height)) || "420px";
  const format = str(useResolve(control.format)) || undefined;

  if (!layout)
    return (
      <Text italic size={200} style={emptyStyle}>
        {t("analysis.tower.empty")}
      </Text>
    );

  const retentionPercent = (layout.retention / layout.top) * 100;

  return (
    <div
      className={mergeClass("mw-tower", controlClass(control))}
      style={{ display: "flex", flexDirection: "column", gap: 4, ...controlStyle(control) }}
    >
      <div style={{ display: "flex", alignItems: "stretch", gap: 8, height }}>
        <div
          style={{
            position: "relative",
            flex: "0 0 auto",
            minWidth: 64,
            borderRight: "1px solid var(--colorNeutralStroke2)",
          }}
        >
          {towerTicks(layout).map((tick) => (
            <div
              key={tick.percent}
              style={{
                position: "absolute",
                right: 0,
                bottom: `${tick.percent}%`,
                transform: "translateY(50%)",
                paddingRight: 6,
                borderRight: "4px solid var(--colorNeutralStroke2)",
                lineHeight: 1,
                fontSize: 11,
                whiteSpace: "nowrap",
                ...hintStyle,
              }}
            >
              {formatValue(tick.amount, format || "N0")}
            </div>
          ))}
        </div>
        <div style={{ position: "relative", flex: "1 1 auto", minWidth: 0 }}>
          {retentionPercent > 0 ? (
            <div
              style={{
                position: "absolute",
                bottom: 0,
                left: 0,
                right: 0,
                height: `${retentionPercent}%`,
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                border: "1px solid var(--colorNeutralStroke2)",
                background:
                  "repeating-linear-gradient(45deg, transparent 0 4px, var(--colorNeutralStroke2) 4px 6px)",
              }}
            >
              <span
                style={{
                  fontSize: 12,
                  padding: "0 6px",
                  borderRadius: 4,
                  background: "var(--colorNeutralBackground1)",
                  ...hintStyle,
                }}
              >
                {retentionLabel}
              </span>
            </div>
          ) : null}
          {layout.bands.map((placement, i) => (
            <TowerBandBox key={i} placement={placement} />
          ))}
        </div>
      </div>
      {currency ? <div style={{ fontSize: 11, ...hintStyle }}>{currency}</div> : null}
    </div>
  );
}

// ---------------------------------------------------------------------------- comparison bars

function ComparisonBarsView({ control }: { control: UiControl }): ReactNode {
  const t = useLocalize();
  const layout = comparisonLayout(rows<ComparisonPair>(useResolve(control.pairs)));
  const leftLegend = useText(control.leftLegend);
  const rightLegend = useText(control.rightLegend);
  const absentText = useText(control.absentText) || t("analysis.comparison.absent");
  const format = str(useResolve(control.format)) || "N0";

  if (!layout)
    return (
      <Text italic size={200} style={emptyStyle}>
        {t("analysis.comparison.empty")}
      </Text>
    );

  // A present value and an absent one must never look alike: only the former gets a bar.
  const side = (percent: number | null, value: number | null, legend: string, weight: string, key: string) => (
    <div key={key} style={{ display: "flex", alignItems: "center", gap: 6, minHeight: 14 }}>
      {percent === null || value === null ? (
        <span style={{ fontSize: 11, fontStyle: "italic", ...hintStyle }}>
          {legend ? `${legend} — ${absentText}` : absentText}
        </span>
      ) : (
        <>
          <span
            style={{
              display: "block",
              height: 14,
              borderRadius: 3,
              flex: "0 0 auto",
              width: `${percent}%`,
              backgroundColor: weight,
            }}
          />
          <span style={{ fontSize: 11, whiteSpace: "nowrap", ...hintStyle }}>
            {legend ? `${legend} ${formatValue(value, format)}` : formatValue(value, format)}
          </span>
        </>
      )}
    </div>
  );

  return (
    <div
      className={mergeClass("mw-comparison-bars", controlClass(control))}
      style={{ display: "flex", flexDirection: "column", gap: 12, ...controlStyle(control) }}
    >
      {layout.rows.map((row, i) => (
        <div key={i} style={{ display: "flex", alignItems: "flex-start", gap: 12 }}>
          <div
            style={{ flex: "0 0 auto", minWidth: 110, fontSize: 12, color: "var(--colorNeutralForeground1)" }}
          >
            {str(row.pair.label)}
          </div>
          <div style={{ flex: "1 1 auto", minWidth: 0, display: "flex", flexDirection: "column", gap: 4 }}>
            {side(row.leftPercent, optionalNum(row.pair.left), leftLegend, "var(--colorBrandBackground)", "l")}
            {side(row.rightPercent, optionalNum(row.pair.right), rightLegend, "var(--colorBrandBackground2)", "r")}
          </div>
        </div>
      ))}
    </div>
  );
}

export const analysisControls = {
  KpiStrip: KpiStripView,
  Tower: TowerView,
  ComparisonBars: ComparisonBarsView,
};
