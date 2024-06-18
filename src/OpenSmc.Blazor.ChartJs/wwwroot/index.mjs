import { C as Chart, r as registerables, p as plugin, c as cloneDeepWith, i as isString } from "./vendor-DwVoHIGZ.mjs";
Chart.register(...registerables, plugin);
Chart.defaults.scales.linear.suggestedMin = 0;
Chart.defaults.elements.line.fill = false;
Chart.defaults.elements.line.tension = 0;
Chart.defaults.plugins.legend.display = false;
Chart.defaults.plugins.datalabels.display = false;
Chart.defaults.font.family = 'roboto, "sans-serif"';
Chart.defaults.font.size = 14;
const instances = /* @__PURE__ */ new Map();
const renderChart = (id, element, config2) => {
  destroyChart(id);
  const ctx = element.querySelector("canvas").getContext("2d");
  const cnf = deserialize(config2);
  const chart = new Chart(ctx, cnf);
  instances.set(id, chart);
};
const destroyChart = (id) => {
  if (instances.has(id)) {
    instances.get(id).destroy();
    instances.delete(id);
  }
};
function deserialize(config) {
  return cloneDeepWith(config, (value) => {
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
  destroyChart,
  renderChart
};
