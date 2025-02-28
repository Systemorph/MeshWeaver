import { Chart, registerables, ChartConfiguration } from 'chart.js';
import 'chartjs-plugin-colorschemes-v3';
import 'chartjs-adapter-moment';
import ChartDataLabels from 'chartjs-plugin-datalabels';
import { cloneDeepWith, isString } from "lodash-es";

Chart.register(...registerables, ChartDataLabels);

Chart.defaults.scales.linear.suggestedMin = 0; // all linear Scales start at 0
Chart.defaults.elements.line.fill = false; // lines default to line, not area
Chart.defaults.elements.line.tension = 0; // lines default to bezier curves. just draw lines as a default instead.
Chart.defaults.plugins.legend.display = false; // default is no Legend.
Chart.defaults.plugins.datalabels.display = false; // default is no DataLabels.
Chart.defaults.plugins.tooltip.enabled = false;

Chart.defaults.font.family = "roboto, \"sans-serif\"";
Chart.defaults.font.size = 12;

// Use the standard Fluent UI text color
Chart.defaults.color = getComputedStyle(document.documentElement).getPropertyValue('--neutral-foreground-rest');

Chart.defaults.plugins.datalabels.formatter = (value, context) =>
    typeof (value) == 'number' ? new Intl.NumberFormat([], { maximumFractionDigits: 0 }).format(value) : value;

export const renderChart = (element: HTMLCanvasElement, config: ChartConfiguration) => {
    const existingChart = Chart.getChart(element);
    const chartConfig = deserialize(config);

    if (existingChart) {
        existingChart.config.data = chartConfig.data;
        existingChart.config.options = chartConfig.options;
        existingChart.update();
    } else {
        const ctx = element.getContext("2d");
        new Chart(ctx, chartConfig);
    }
}


function deserialize(data: unknown) {
    return cloneDeepWith(data, value => {
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

