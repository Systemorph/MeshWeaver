import { Chart, registerables, ChartConfiguration } from 'chart.js';
import 'chartjs-plugin-colorschemes-v3'
import 'chartjs-adapter-moment';
import ChartDataLabels from 'chartjs-plugin-datalabels';
import { useEffect, useRef } from "react";
import { cloneDeep, isObjectLike, keys } from "lodash";
import { evalJs, REGEXPS } from "@open-smc/utils/src/evalJs";

import { ControlView } from "../ControlDef";

Chart.register(...registerables, ChartDataLabels);

export interface ChartView extends ControlView {
    data: ChartConfiguration;
}

export default function ChartControl({id, data, style}: ChartView) {
    const elementRef = useRef<HTMLDivElement>(null);

    style = style?.width ? style : {...style, width: '100%', position: 'relative'};

    useEffect(() => {
        try {
            cleanup(data);

            const ctx = elementRef.current
                .querySelector<HTMLCanvasElement>("canvas")
                .getContext("2d");

            const config = cloneDeep(data);

            evalJs(config, REGEXPS.func);

            const chart = new Chart(ctx, config);

            return () => {
                chart.destroy();
            }
        } catch (error) {
            console.error(error);
        }
    }, [data]);

    return (
        <div id={id} className="chart-container" ref={elementRef} style={style}>
            <canvas></canvas>
        </div>
    );
}

Chart.defaults.scales.linear.suggestedMin = 0; // all linear Scales start at 0
Chart.defaults.elements.line.fill = false; // lines default to line, not area
Chart.defaults.elements.line.tension = 0; // lines default to bezier curves. just draw lines as a default instead.
Chart.defaults.plugins.legend.display = false; // default is no Legend.
Chart.defaults.plugins.datalabels.display = false; // default is no DataLabels.

Chart.defaults.font.family = "roboto, \"sans-serif\"";
Chart.defaults.font.size = 14;

function cleanup(data: any) {
    if (data["$type"]) {
        delete data["$type"];
    }

    keys(data).forEach(key => {
        if (isObjectLike(data[key])) {
            cleanup(data[key]);
        }
    })
}