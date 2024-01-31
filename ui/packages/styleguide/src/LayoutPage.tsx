import { Sandbox } from "@open-smc/sandbox/Sandbox";
import {makeStack} from "@open-smc/sandbox/LayoutStack";
import {makeChart} from "@open-smc/sandbox/Chart";
import waterfall1 from "./charts/TarParWaterfallByBusinessSegmentChart.json";
import waterfall2 from "./charts/TarParWaterfallByReportTypeChart.json";
import lineChart from "./charts/TarParLineChart.json";

const layoutTemplate = makeStack()
    .withView(makeChart()
        .withConfig(waterfall1 as any),
        (builder) => builder
            .withStyle(style => style
                .withWidth('400px')
            )
    )
    .withView(makeChart()
        .withConfig(lineChart as any),
        (builder) => builder
            .withStyle(style => style
                .withWidth('300px')
            )
    )
    .build();

export function LayoutPage() {
    return (
        <div>
            <div>
                <h3>Sample html</h3>
                <Sandbox root={layoutTemplate}/>
            </div>
        </div>
    );
}