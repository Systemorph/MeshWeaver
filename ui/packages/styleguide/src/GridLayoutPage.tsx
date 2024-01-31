import { Sandbox } from "@open-smc/sandbox/Sandbox";
import { makeStack } from "@open-smc/sandbox/LayoutStack";
import { makeChart } from "@open-smc/sandbox/Chart";
import waterfall1 from "./charts/TarParWaterfallByBusinessSegmentChart.json";
import waterfall2 from "./charts/TarParWaterfallByReportTypeChart.json";
import lineChart from "./charts/TarParLineChart.json";
import styles from "./sample.module.scss";
import { makeHtml } from "@open-smc/sandbox/Html";

const gridLayoutTemplate = makeStack()
    .withView(makeChart()
        .withConfig(waterfall1 as any)
    )
    .withView(makeChart()
        .withConfig(waterfall2 as any)
    )
    .withView(makeChart()
        .withConfig(lineChart as any)
    )
    .withView(
        makeHtml(`
            <div class="bigNumber">
                <div>
                    <h4>Economic Profit</h4>
                    <p>144 mCHF</p>
                </div>
            </div>
        `).withStyle(style => style
            .withHeight('100%')
        )
    )
    .withSkin('GridLayout')
    .withColumnCount(2)
    .build();

export function GridLayoutPage() {
    return (
        <div>
            <div className={styles.gridSample}>
                <h2>Grid Layout</h2>
                <p>Grid skin lets you organize content into a grid with <var>columnCount</var> number of columns.</p>
                <p>Red lines demonstrate the borders of the grid cells.</p>
                <Sandbox root={gridLayoutTemplate}/>
            </div>
        </div>
    );
}
