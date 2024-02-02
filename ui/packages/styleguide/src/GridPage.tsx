import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import { makeGridOptions } from "./makeGridOptions";
import { makeGrid } from "@open-smc/sandbox/src/Grid";

import gridConfig from "./grids/EconomicProfitReport.json";

const grid = makeGrid()
    .withOptions(gridConfig as any)
    .withStyle(style => style.withHeight("400px"))
    .build();

export function GridPage() {
    return (
        <div>
            <Sandbox root={grid}/>
        </div>
    );
}

