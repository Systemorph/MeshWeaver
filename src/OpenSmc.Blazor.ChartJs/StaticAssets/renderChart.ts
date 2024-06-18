import { Chart, registerables, ChartConfiguration } from 'chart.js';
import 'chartjs-plugin-colorschemes-v3'
import 'chartjs-adapter-moment';
import ChartDataLabels from 'chartjs-plugin-datalabels';
import { cloneDeepWith, isString } from "lodash-es";

Chart.register(...registerables, ChartDataLabels);

Chart.defaults.scales.linear.suggestedMin = 0; // all linear Scales start at 0
Chart.defaults.elements.line.fill = false; // lines default to line, not area
Chart.defaults.elements.line.tension = 0; // lines default to bezier curves. just draw lines as a default instead.
Chart.defaults.plugins.legend.display = false; // default is no Legend.
Chart.defaults.plugins.datalabels.display = false; // default is no DataLabels.

Chart.defaults.font.family = "roboto, \"sans-serif\"";
Chart.defaults.font.size = 14;

const instances = new Map<string, Chart>();

export const renderChart = (id, element, config) => {
    destroyChart(id);

    const ctx = element.querySelector("canvas").getContext("2d");

    const cnf = deserialize(config);

    const chart = new Chart(ctx, cnf);
    instances.set(id, chart);
}

export const destroyChart = id => {
    if (instances.has(id)) {
        instances.get(id).destroy();
        instances.delete(id);
    }
}

function deserialize(config: ChartConfiguration) {
    return cloneDeepWith(config, value => {
        if (isString(value) && funcRegexps.some(regexp => regexp.test(value))) {
            try {
                return eval(`(${value})`);
            } catch (error) {
                console.error("Error evaluating function string:", error);
                return null;
            }
        }
    });
}

const funcRegexps = [
    /^function\b/,
    /^\(function\b/,
    /^\s*(\s*[a-zA-Z]\w*|\(\s*[a-zA-Z]\w*(\s*,\s*[a-zA-Z]\w*)*\s*\))\s*=>/
];

