import { makeChart } from "@open-smc/sandbox/src/Chart";
import { Sandbox } from "@open-smc/sandbox/src/Sandbox";

import barConfig from "./charts/bar.json";
import waterfallConfig from "./charts/EconomicProfitChart.json";
import waterfallConfig2 from "./charts/TarParChart.json";

const barChart = makeChart()
    .withConfig(barConfig as any)
    .withStyle(
        style =>
            style
                .withWidth("500px")
    )
    .build();

const waterfallChart = makeChart()
    .withConfig(waterfallConfig as any)
    .withStyle(
        style =>
            style
                .withWidth("500px")
    )
    .build();

const waterfallChart2 = makeChart()
    .withConfig(waterfallConfig2 as any)
    .withStyle(
        style =>
            style
                .withWidth("500px")
    )
    .build();
	
export function ChartPage() {
    return (
        <div>
            <div>
                <h3>Bar chart</h3>
                <Sandbox root={barChart}/>
            </div>
            <div>
                <h3>Waterfall</h3>
                <Sandbox root={waterfallChart}/>
            </div>
            <div>
                <h3>Waterfall2</h3>
                <Sandbox root={waterfallChart2}/>
            </div>
		</div>
    );
}