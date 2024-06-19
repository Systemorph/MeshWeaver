import { C as Chart, r as registerables, p as plugin, c as cloneDeepWith, i as isString } from "./vendor-DwVoHIGZ.mjs";
Chart.register(...registerables, plugin);
Chart.defaults.scales.linear.suggestedMin = 0;
Chart.defaults.elements.line.fill = false;
Chart.defaults.elements.line.tension = 0;
Chart.defaults.plugins.legend.display = false;
Chart.defaults.plugins.datalabels.display = false;
Chart.defaults.font.family = 'roboto, "sans-serif"';
Chart.defaults.font.size = 14;
const renderChart = (element, config) => {
  var _a;
  (_a = Chart.getChart(element)) == null ? void 0 : _a.destroy;
  const ctx = element.getContext("2d");
  const chartConfig = deserialize(config);
  new Chart(ctx, chartConfig).attached;
};
function deserialize(data) {
  return cloneDeepWith(data, (value) => {
    if (isString(value) && funcRegexps.some((regexp) => regexp.test(value))) {
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
export {
  renderChart
};
