/**
 * Load Chart.js (if needed) and render the chart in the provided element
 * @param {HTMLCanvasElement} element - The canvas element to render the chart
 * @param {Object} config - The chart configuration
 * @returns {Promise<void>} - A promise that resolves when chart rendering is complete
 */
export async function renderChart(element, config) {
    console.log("Starting Chart.js loading and rendering process");

    // Check if Chart.js is already loaded
    if (!window.Chart) {
        // Load Chart.js from CDN
        const loaded = await loadChartJs();
        if (!loaded) {
            console.error("Failed to load Chart.js");
            return;
        }
    }

    // Configure Chart.js defaults
    configureChartDefaults();

    // Render or update the chart
    const existingChart = window.Chart.getChart(element);
    const chartConfig = deserialize(config);

    if (existingChart) {
        existingChart.config.data = chartConfig.data;
        existingChart.config.options = chartConfig.options;
        existingChart.update();
    } else {
        const ctx = element.getContext("2d");
        new window.Chart(ctx, chartConfig);
    }
}

/**
 * Load Chart.js and required plugins from CDN
 * @returns {Promise<boolean>} - A promise that resolves to true if loading was successful
 */
async function loadChartJs() {
    try {
        // Load Chart.js core
        await loadScript('https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.js', 'chartjs-script');

        // Load Chart.js plugins
        await loadScript('https://cdn.jsdelivr.net/npm/chartjs-plugin-datalabels@2.2.0/dist/chartjs-plugin-datalabels.min.js', 'chartjs-datalabels-script');
        await loadScript('https://cdn.jsdelivr.net/npm/chartjs-adapter-moment@1.0.1/dist/chartjs-adapter-moment.min.js', 'chartjs-moment-script');

        // Load moment.js for the adapter
        await loadScript('https://cdn.jsdelivr.net/npm/moment@2.29.4/moment.min.js', 'moment-script');

        // Register plugins
        if (window.Chart && window.ChartDataLabels) {
            window.Chart.register(window.ChartDataLabels);
        }

        return true;
    } catch (error) {
        console.error("Error loading Chart.js:", error);
        return false;
    }
}

/**
 * Load a script from CDN
 * @param {string} src - The script source URL
 * @param {string} id - The script element ID
 * @returns {Promise<void>} - A promise that resolves when the script is loaded
 */
function loadScript(src, id) {
    return new Promise((resolve, reject) => {
        // Check if script is already loaded
        if (document.getElementById(id)) {
            resolve();
            return;
        }

        const script = document.createElement('script');
        script.src = src;
        script.id = id;
        script.onload = () => resolve();
        script.onerror = () => reject(new Error(`Failed to load script: ${src}`));
        document.head.appendChild(script);
    });
}

/**
 * Configure Chart.js default settings
 */
function configureChartDefaults() {
    if (!window.Chart) return;

    const Chart = window.Chart;

    // Set default configurations
    Chart.defaults.scales.linear.suggestedMin = 0; // all linear Scales start at 0
    Chart.defaults.elements.line.fill = false; // lines default to line, not area
    Chart.defaults.elements.line.tension = 0; // lines default to straight lines instead of bezier curves
    Chart.defaults.plugins.legend.display = false; // default is no Legend
    Chart.defaults.plugins.datalabels.display = false; // default is no DataLabels
    Chart.defaults.plugins.tooltip.enabled = false;

    Chart.defaults.font.family = "roboto, \"sans-serif\"";
    Chart.defaults.font.size = 12;

    // Use the standard Fluent UI text color
    Chart.defaults.color = getComputedStyle(document.documentElement).getPropertyValue('--neutral-foreground-rest');

    // Configure data labels formatter
    Chart.defaults.plugins.datalabels.formatter = (value, context) =>
        typeof value == 'number' ? new Intl.NumberFormat([], { maximumFractionDigits: 0 }).format(value) : value;
}

/**
 * Deserialize chart configuration, evaluating function strings
 * @param {Object} data - The configuration data
 * @returns {Object} - The deserialized configuration
 */
function deserialize(data) {
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

/**
 * Simple deep clone with customizer function (simplified lodash-like implementation)
 * @param {*} value - The value to clone
 * @param {Function} customizer - The customizer function
 * @returns {*} - The cloned value
 */
function cloneDeepWith(value, customizer) {
    function cloneDeep(val) {
        const result = customizer(val);
        if (result !== undefined) {
            return result;
        }

        if (val === null || typeof val !== 'object') {
            return val;
        }

        if (Array.isArray(val)) {
            return val.map(cloneDeep);
        }

        if (val instanceof Date) {
            return new Date(val.getTime());
        }

        if (typeof val === 'object') {
            const cloned = {};
            for (const key in val) {
                if (val.hasOwnProperty(key)) {
                    cloned[key] = cloneDeep(val[key]);
                }
            }
            return cloned;
        }

        return val;
    }

    return cloneDeep(value);
}

/**
 * Check if value is a string (simplified lodash-like implementation)
 * @param {*} value - The value to check
 * @returns {boolean} - True if value is a string
 */
function isString(value) {
    return typeof value === 'string';
}

/**
 * Regular expressions to detect function strings
 */
const funcRegexps = [
    /^function\b/,
    /^\(function\b/,
    /^\s*(\s*[a-zA-Z]\w*|\(\s*[a-zA-Z]\w*(\s*,\s*[a-zA-Z]\w*)*\s*\))\s*=>/
];
