import type { ChartConfiguration } from 'chart.js';
import type { ChartView } from "@open-smc/application/src/controls/ChartControl";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Chart extends ControlBase implements ChartView {
    data: ChartConfiguration;
    
    constructor() {
        super("ChartControl");
    }
}

export class ChartBuilder extends ControlBuilderBase<Chart> {
    constructor() {
        super(Chart);
    }

    withConfig(config: ChartConfiguration) {
        this.data.data = config;
        return this;
    }
}

export const makeChart = () => new ChartBuilder();