// The standard analysis views (#745), native. The RN twins of
// clients/react/src/controls/analysis.tsx and of the Blazor
// src/MeshWeaver.Blazor/Components/{KpiStrip,Tower,ComparisonBars}View.razor.
//
// No geometry lives here. `towerLayout` / `comparisonLayout` / `towerTicks` come from the shared
// renderer core (analysisLayout.ts, itself the port of TowerControl.Layout /
// ComparisonBarsControl.Layout), so a band sits where the framework says it sits on every client.
// These components only place native <View>s at the percentages they are handed.
//
// Percent-of-parent works in RN's flexbox the same way it does in CSS, so the tower is the same
// absolutely-positioned stack the web pack draws — no SVG, no second geometry.

import { View, Text, Pressable, StyleSheet } from "react-native";
import {
  analysisRows,
  clamp,
  comparisonLayout,
  formatValue,
  navigableHref,
  num,
  optionalNum,
  str,
  towerLayout,
  towerTicks,
  useEmit,
  useLocalize,
  useResolve,
  useScope,
  type ComparisonPairWire,
  type ControlComponent,
  type Json,
  type TowerBandWire,
} from "@meshweaver/react/core";

interface KpiItemWire {
  label?: Json;
  value?: Json;
  hint?: Json;
}

// RN's DimensionValue accepts a `${number}%` template-literal type, and a plain `${x}%` expression
// widens to `string` outside a contextual position. Build the value through here so every percentage
// lands as the type RN expects rather than depending on inference through a style array.
const pct = (v: number) => `${v}%` as `${number}%`;

const KpiStrip: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const items = analysisRows<KpiItemWire>(useResolve(control.items));

  if (items.length === 0) return <Text style={styles.empty}>{t("analysis.kpi.empty")}</Text>;

  return (
    <View style={styles.strip}>
      {items.map((item, i) => (
        <View key={i} style={styles.tile}>
          <Text style={styles.tileLabel}>{str(item.label ?? null)}</Text>
          <Text style={styles.tileValue}>{str(item.value ?? null)}</Text>
          {item.hint ? <Text style={styles.hint}>{str(item.hint)}</Text> : null}
        </View>
      ))}
    </View>
  );
};

const Tower: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const emit = useEmit();
  const { area } = useScope();
  const layout = towerLayout(analysisRows<TowerBandWire>(useResolve(control.bands)));
  const currency = str(useResolve(control.currency));
  const retentionLabel = str(useResolve(control.retentionLabel)) || t("analysis.tower.retained");
  const format = str(useResolve(control.format)) || "N0";

  if (!layout) return <Text style={styles.empty}>{t("analysis.tower.empty")}</Text>;

  const retentionPercent = (layout.retention / layout.top) * 100;

  return (
    <View style={{ gap: 4 }}>
      <View style={styles.plot}>
        <View style={styles.axis}>
          {towerTicks(layout).map((tick) => (
            <View key={tick.percent} style={[styles.tick, { bottom: pct(tick.percent) }]}>
              <Text style={styles.tickLabel}>{formatValue(tick.amount, format)}</Text>
            </View>
          ))}
        </View>
        <View style={styles.stack}>
          {retentionPercent > 0 ? (
            <View style={[styles.retention, { height: pct(retentionPercent) }]}>
              <Text style={styles.hint}>{retentionLabel}</Text>
            </View>
          ) : null}
          {layout.bands.map((placement, i) => {
            const href = navigableHref(str(placement.band.href));
            const body = (
              <>
                <View style={[styles.bandShare, { width: pct(clamp(num(placement.band.share), 0, 1) * 100) }]} />
                <View style={styles.bandText}>
                  <Text style={styles.bandLabel} numberOfLines={1}>
                    {str(placement.band.label)}
                  </Text>
                  <Text style={styles.hint} numberOfLines={1}>
                    {str(placement.band.terms)}
                  </Text>
                </View>
              </>
            );
            const box = [
              styles.band,
              { bottom: pct(placement.bottomPercent), height: pct(placement.heightPercent) },
            ];
            // A linked band emits the same click the other RN leaves emit — the shell routes it.
            return href ? (
              <Pressable key={i} style={box} onPress={() => emit({ kind: "click", area })}>
                {body}
              </Pressable>
            ) : (
              <View key={i} style={box}>
                {body}
              </View>
            );
          })}
        </View>
      </View>
      {currency ? <Text style={styles.hint}>{currency}</Text> : null}
    </View>
  );
};

const ComparisonBars: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const layout = comparisonLayout(analysisRows<ComparisonPairWire>(useResolve(control.pairs)));
  const leftLegend = str(useResolve(control.leftLegend));
  const rightLegend = str(useResolve(control.rightLegend));
  const absentText = str(useResolve(control.absentText)) || t("analysis.comparison.absent");
  const format = str(useResolve(control.format)) || "N0";

  if (!layout) return <Text style={styles.empty}>{t("analysis.comparison.empty")}</Text>;

  // A present value and an absent one must never look alike: only the former gets a bar.
  const side = (percent: number | null, value: number | null, legend: string, color: string, key: string) => (
    <View key={key} style={styles.side}>
      {percent === null || value === null ? (
        <Text style={styles.absent}>{legend ? `${legend} — ${absentText}` : absentText}</Text>
      ) : (
        <>
          <View style={[styles.bar, { width: pct(percent), backgroundColor: color }]} />
          <Text style={styles.hint}>
            {legend ? `${legend} ${formatValue(value, format)}` : formatValue(value, format)}
          </Text>
        </>
      )}
    </View>
  );

  return (
    <View style={{ gap: 12 }}>
      {layout.rows.map((row, i) => (
        <View key={i} style={styles.cmpRow}>
          <Text style={styles.measure}>{str(row.pair.label)}</Text>
          <View style={styles.bars}>
            {side(row.leftPercent, optionalNum(row.pair.left), leftLegend, "#0f6cbd", "l")}
            {side(row.rightPercent, optionalNum(row.pair.right), rightLegend, "#a3c7e8", "r")}
          </View>
        </View>
      ))}
    </View>
  );
};

export const rnAnalysisControls: Record<string, ControlComponent> = {
  KpiStrip,
  Tower,
  ComparisonBars,
};

const styles = StyleSheet.create({
  empty: { fontSize: 12, fontStyle: "italic", color: "#616161" },
  hint: { fontSize: 11, color: "#616161" },
  // KPI strip
  strip: { flexDirection: "row", flexWrap: "wrap", gap: 10 },
  tile: {
    flexGrow: 1,
    minWidth: 140,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#e1e1e1",
    borderRadius: 10,
    padding: 10,
  },
  tileLabel: { fontSize: 11, letterSpacing: 1, color: "#616161", textTransform: "uppercase" },
  tileValue: { fontSize: 20, fontWeight: "700", color: "#242424", marginTop: 2 },
  // Tower
  plot: { flexDirection: "row", alignItems: "stretch", gap: 8, height: 360 },
  axis: { width: 72, borderRightWidth: StyleSheet.hairlineWidth, borderRightColor: "#e1e1e1" },
  tick: { position: "absolute", right: 4 },
  tickLabel: { fontSize: 11, color: "#616161" },
  stack: { flex: 1 },
  retention: {
    position: "absolute",
    left: 0,
    right: 0,
    bottom: 0,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#e1e1e1",
    backgroundColor: "#f5f5f5",
  },
  band: {
    position: "absolute",
    left: 0,
    right: 0,
    overflow: "hidden",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#e1e1e1",
    backgroundColor: "#e8f1fa",
  },
  bandShare: { position: "absolute", top: 0, bottom: 0, left: 0, backgroundColor: "#a3c7e8" },
  bandText: { padding: 6 },
  bandLabel: { fontSize: 13, fontWeight: "600", color: "#242424" },
  // Comparison bars
  cmpRow: { flexDirection: "row", alignItems: "flex-start", gap: 12 },
  measure: { minWidth: 110, fontSize: 12, color: "#242424" },
  bars: { flex: 1, gap: 4 },
  side: { flexDirection: "row", alignItems: "center", gap: 6, minHeight: 14 },
  bar: { height: 14, borderRadius: 3 },
  absent: { fontSize: 11, fontStyle: "italic", color: "#616161" },
});
