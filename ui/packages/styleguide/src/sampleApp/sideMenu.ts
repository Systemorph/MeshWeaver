import { makeGrid } from "@open-smc/sandbox/src/Grid";
import { getOrAdd } from "@open-smc/utils/src/getOrAdd";
import { makeStack } from "@open-smc/sandbox/src/LayoutStack";
import { makeMenuItem } from "@open-smc/sandbox/src/MenuItem";
import { sampleApp } from "./sampleApp";
import { makeChart } from "@open-smc/sandbox/src/Chart";
import { makeIcon } from "@open-smc/sandbox/src/Icon";
import { brandeisBlue } from "@open-smc/application/src/colors";

import gridTarPar from "../grids/TarParReport.json";
import gridEconomicProfit from "../grids/EconomicProfitReport.json";
import chartTarPar from "../charts/TarParChart.json";
import chartEconomicProfit from "../charts/EconomicProfitChart.json";
import lineChart from "../charts/TarParLineChart.json";
import waterfall1 from "../charts/TarParWaterfallByBusinessSegmentChart.json";
import waterfall2 from "../charts/TarParWaterfallByReportTypeChart.json";
import { makeHtml } from "@open-smc/sandbox/src/Html";
import { ControlDef } from "@open-smc/application/src/ControlDef";

export const sideMenu = makeStack()
    .withView(
        makeIcon("systemorph-fill")
            .withSize("L")
            .withColor(brandeisBlue)
            .withStyle(style => style.withMargin("0 0 12px"))
            .withClickAction(() => alert("clicked"))
    )
    .withView(
        makeMenuItem()
            .withTitle("Tar Par")
            .withIcon("table")
            .withClickAction(clickActionTarPar, "tarPar")
    )
    .withView(
        makeMenuItem()
            .withTitle("Economic Profit")
            .withIcon("money")
            .withClickAction(clickActionEconomicProfit, "economicProfit")
    )
    .withView(
        makeMenuItem()
            .withTitle("Linear Chart")
            .withIcon("linear-chart")
            .withClickAction(clickActionLineChart, "lineChart")
    )
    .withView(
        makeMenuItem()
            .withTitle("Waterfalls")
            .withIcon("waterfall-chart")
            .withClickAction(clickActionWaterfalls, "waterfalls")
    )
    .withView(
        makeMenuItem()
            .withTitle("Scenarios")
            .withIcon("outline")
            .withClickAction(clickScenarios)
    )
    .withSkin("SideMenu");

const reports = new Map<string, ControlDef>();

function clickActionEconomicProfit(payload: unknown) {
    sampleApp.setArea(
        mainWindowAreas.main,
        getOrAdd(
            reports,
            payload,
            () => makeEconomicProfitContent().build()
        )
    )
}

function clickActionTarPar(payload: unknown) {
    sampleApp.setArea(
        mainWindowAreas.main,
        getOrAdd(
            reports,
            payload,
            () => makeTarParContent().build()
        )
    )
}

function clickActionLineChart(payload: unknown) {
    sampleApp.setArea(
        mainWindowAreas.main,
        getOrAdd(
            reports,
            payload,
            () => makeLineChartContent().build()
        )
    )
}

function clickActionWaterfalls(payload: unknown) {
    sampleApp.setArea(
        mainWindowAreas.main,
        getOrAdd(
            reports,
            payload,
            () => makeWaterfallsContent().build()
        )
    )
}

function clickScenarios() {
    sampleApp.setArea(
        mainWindowAreas.main,
        makeScenarios().build()
    )
}

function makeEconomicProfitContent() {
    return makeStack()
        .withView(makeEconomicProfitReport())
        //.withView(makeEconomicProfitChart())
        ;
}

function makeEconomicProfitReport() {
    return makeGrid()
        .withOptions(gridEconomicProfit as any)
        .withStyle(style => style.withHeight("400px"))
        ;
}

function makeEconomicProfitChart() {
    return makeChart()
        .withConfig(chartEconomicProfit as any)
        //.withStyle(style => style.withWidth("500px"))
        ;
}

function makeTarParContent() {
    return makeStack()
        .withView(makeTarParReport())
        // .withView(makeTarParChart())
        ;
}

function makeTarParReport() {
    return makeGrid()
        .withOptions(gridTarPar as any)
        .withStyle(style => style.withHeight("400px"))
        ;
}

function makeTarParChart() {
    return makeChart()
        .withConfig(chartTarPar as any)
        ;
}

function makeLineChartContent() {
    return makeStack()
        .withSkin("HorizontalPanelEqualCols")
        .withStyle(style => style.withFlexWrap('wrap'))
        .withView(makeStack()
            .withView(lineChartTitle())
            .withView(lineChartView())
        )
}

function lineChartTitle() {
    return makeHtml().withData("<h3>Financial – Economic profit</h3><h5>mCHF</h5>");
}

function lineChartView() {
    return makeChart().withConfig(lineChart as any)
        .withStyle(style => style.withMinWidth('400px'));
}

function makeWaterfallsContent() {
    return makeStack()
        .withSkin("HorizontalPanelEqualCols")
        .withStyle(style => style.withFlexWrap('wrap'))
        .withView(makeChart()
            .withConfig(waterfall1 as any)
        )
        .withView(makeChart()
            .withConfig(waterfall2 as any)
        );
}

function makeScenarios() {
    return makeHtml(
        `
        <div class="scenarios-overview">
                <img src="https://storage.systemorph.cloud/content/MobiDemo/Images/scenarios-1.png" alt="" />
                <h2>Interest Rate Change</h2>
                <p>A sudden, unexpected shift of the yield curve of predefined form and size.  The most widespread (but not
            most realistic) definition is a parallel shift of the yield curve, either upward or downward, by an even number
            of basis points such as 100 bp.</p>

            <hr/>

            <img src="https://storage.systemorph.cloud/content/MobiDemo/Images/scenarios-2.png" alt="" />
            <h2>Monetary Policy Change</h2>
            <p>
            Sudden change of the monetary policy by the central bank (or the government).
            This often means a tightening of the lending conditions to major financial  institutions and not only affects
            interest rates but also a number of key macroeconomic and financial factors such as GDP growth or equity indices.
            An exact definition is required, including the affected driver variables (for both insurance and financial risks)
            and all parameters.
            </p>

            <hr/>

            <img src="https://storage.systemorph.cloud/content/MobiDemo/Images/scenarios-3.png" alt="" />
            <h2>Monetary Policy Change</h2>
            <p>
            Represents the unbiased expectation of future development.
            </p>

        </div>
        `
    );
}
