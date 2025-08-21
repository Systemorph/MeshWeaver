// ChartView.razor.js
// This file provides the JavaScript functionality for Chart.js integration

// Keep track of chart instances for theme updates
const chartInstances = new Map();

// Loading state management for concurrent requests
let chartJsLoadingPromise = null;
let chartJsLoaded = false;

/**
 * Load Chart.js (if needed) and render the chart in the provided element
 * @param {HTMLCanvasElement} element - The canvas element to render the chart
 * @param {Object} config - The chart configuration
 * @returns {Promise<void>} - A promise that resolves when chart rendering is complete
 */
export async function renderChart(element, config) {
    console.log("Starting Chart.js loading and rendering process");
    console.log("Config received:", config);

    // Check if Chart.js is already loaded
    if (!window.Chart) {
        // Use shared loading promise to prevent concurrent loading
        if (!chartJsLoadingPromise) {
            chartJsLoadingPromise = loadChartJs();
        }

        const loaded = await chartJsLoadingPromise;
        if (!loaded) {
            console.error("Failed to load Chart.js");
            return;
        }
    } else if (!chartJsLoaded) {
        // Chart.js is loaded but we haven't configured it yet
        configureChartDefaults();
        chartJsLoaded = true;
    }    // Render or update the chart
    try {
        const existingChart = window.Chart.getChart(element);
        const chartConfig = deserialize(config);

        console.log("Deserialized config:", chartConfig);
        console.log("Chart options:", chartConfig.options);
        console.log("Legend config:", chartConfig.options?.plugins?.legend);

        // Ensure the chart config has the correct structure for Chart.js
        // Chart.js expects { type, data, options } but we might have a nested structure
        let finalConfig;
        if (chartConfig.data && chartConfig.options) {
            // Extract type from the first dataset if not at root level
            const chartType = chartConfig.data.datasets?.[0]?.type || 'bar';
            finalConfig = {
                type: chartType,
                data: chartConfig.data,
                options: chartConfig.options
            };
        } else {
            finalConfig = chartConfig;
        }

        // Apply responsive configuration if not already set
        if (!finalConfig.options) {
            finalConfig.options = {};
        }
        if (!finalConfig.options.responsive) {
            finalConfig.options.responsive = true;
        }
        if (!finalConfig.options.maintainAspectRatio) {
            finalConfig.options.maintainAspectRatio = false;
        }
        
        // Apply mobile-friendly scale configuration only on narrow screens
        const isNarrowScreen = window.innerWidth < 768;
        
        if (isNarrowScreen) {
            if (!finalConfig.options.scales) {
                finalConfig.options.scales = {};
            }
            
            // Configure x-axis for better label handling on narrow screens
            if (!finalConfig.options.scales.x) {
                finalConfig.options.scales.x = {};
            }
            if (!finalConfig.options.scales.x.ticks) {
                finalConfig.options.scales.x.ticks = {};
            }
            
            // Only override rotation if not already specifically set
            if (finalConfig.options.scales.x.ticks.maxRotation === undefined) {
                finalConfig.options.scales.x.ticks.maxRotation = 45;
            }
            if (finalConfig.options.scales.x.ticks.minRotation === undefined) {
                finalConfig.options.scales.x.ticks.minRotation = 0;
            }
            
            // Add callback for label truncation on narrow screens only if no callback exists
            if (!finalConfig.options.scales.x.ticks.callback) {
                finalConfig.options.scales.x.ticks.callback = function(value, index, values) {
                    const label = this.getLabelForValue(value);
                    if (label && label.length > 10) {
                        return label.substring(0, 8) + '...';
                    }
                    return label;
                };
            }
            
            // Configure data labels for narrow screens
            if (!finalConfig.options.plugins) {
                finalConfig.options.plugins = {};
            }
            if (!finalConfig.options.plugins.datalabels) {
                finalConfig.options.plugins.datalabels = {};
            }
            
            // Reduce font size and adjust positioning for data labels on narrow screens
            finalConfig.options.plugins.datalabels.font = {
                size: 8
            };
            finalConfig.options.plugins.datalabels.rotation = -90; // Rotate labels vertically
            finalConfig.options.plugins.datalabels.anchor = 'end';
            finalConfig.options.plugins.datalabels.align = 'top';
        }

        console.log("Final config for Chart.js:", finalConfig);

        if (existingChart) {
            existingChart.config.data = finalConfig.data;
            existingChart.config.options = finalConfig.options;
            existingChart.update();
            // Update our tracking
            chartInstances.set(element.id || element, existingChart);
        } else {
            const ctx = element.getContext("2d");
            const newChart = new window.Chart(ctx, finalConfig);
            // Track this chart instance
            chartInstances.set(element.id || element, newChart);
            
            // Set up resize listener for this chart
            setupResizeListener(newChart, element);
        }
    } catch (error) {
        console.error("Error rendering chart:", error);
        console.error("Element:", element);
        console.error("Config:", config);
        throw error; // Re-throw to let calling code handle it
    }
}

/**
 * Load Chart.js and required plugins from CDN
 * @returns {Promise<boolean>} - A promise that resolves to true if loading was successful
 */
async function loadChartJs() {
    try {
        console.log("Starting Chart.js loading process");

        // Load Chart.js core
        await loadScript('https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.js', 'chartjs-script');

        // Wait for Chart.js to be available with timeout
        await waitForGlobal('Chart', 5000);

        // Load DataLabels plugin
        await loadScript('https://cdn.jsdelivr.net/npm/chartjs-plugin-datalabels@2.2.0/dist/chartjs-plugin-datalabels.min.js', 'chartjs-datalabels-script');

        // Wait for DataLabels to be available with timeout
        await waitForGlobal('ChartDataLabels', 3000);

        // Load moment.js for the adapter
        await loadScript('https://cdn.jsdelivr.net/npm/moment@2.29.4/moment.min.js', 'moment-script');

        // Register plugins
        if (window.Chart && window.ChartDataLabels) {
            window.Chart.register(window.ChartDataLabels);
            console.log('Chart.js DataLabels plugin registered successfully');
        } else {
            console.warn('Chart.js DataLabels plugin not available for registration');
        }

        // Configure defaults after plugins are registered
        if (window.Chart) {
            configureChartDefaults();
            chartJsLoaded = true;
        } else {
            throw new Error('Chart.js not available for configuration');
        }

        // Set up theme change callback once Chart.js is loaded
        if (window.themeHandler && typeof window.themeHandler.registerThemeChangeCallback === 'function') {
            window.themeHandler.registerThemeChangeCallback(() => {
                updateChartTheme();
            });
        } else {
            console.warn('Theme handler not available for Chart.js. Theme changes will not be applied automatically.');
        }

        console.log("Chart.js loading completed successfully");
        return true;
    } catch (error) {
        console.error("Error loading Chart.js:", error);
        chartJsLoadingPromise = null; // Reset on error to allow retry
        return false;
    }
}

/**
 * Load a script from CDN with better error handling and concurrency support
 * @param {string} src - The script source URL
 * @param {string} id - The script element ID
 * @returns {Promise<void>} - A promise that resolves when the script is loaded
 */
function loadScript(src, id) {
    return new Promise((resolve, reject) => {
        // Check if script is already loaded
        const existingScript = document.getElementById(id);
        if (existingScript) {
            // If script exists and has loaded successfully, resolve immediately
            if (existingScript.dataset.loaded === 'true') {
                resolve();
                return;
            }
            // If script exists but is still loading, wait for it
            if (existingScript.dataset.loading === 'true') {
                existingScript.addEventListener('load', () => resolve());
                existingScript.addEventListener('error', () => reject(new Error(`Failed to load script: ${src}`)));
                return;
            }
        }

        const script = document.createElement('script');
        script.src = src;
        script.id = id;
        script.dataset.loading = 'true';

        script.onload = () => {
            script.dataset.loaded = 'true';
            script.dataset.loading = 'false';
            resolve();
        };

        script.onerror = () => {
            script.dataset.loading = 'false';
            reject(new Error(`Failed to load script: ${src}`));
        };

        document.head.appendChild(script);
    });
}

/**
 * Wait for a global variable to become available with timeout
 * @param {string} globalName - The name of the global variable to wait for
 * @param {number} timeout - Timeout in milliseconds
 * @returns {Promise<void>} - A promise that resolves when the global is available
 */
function waitForGlobal(globalName, timeout = 5000) {
    return new Promise((resolve, reject) => {
        if (window[globalName]) {
            resolve();
            return;
        }

        const startTime = Date.now();
        const checkInterval = setInterval(() => {
            if (window[globalName]) {
                clearInterval(checkInterval);
                resolve();
            } else if (Date.now() - startTime > timeout) {
                clearInterval(checkInterval);
                reject(new Error(`Timeout waiting for ${globalName} to load`));
            }
        }, 100);
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
    
    // Configure responsive behavior
    Chart.defaults.responsive = true;
    Chart.defaults.maintainAspectRatio = false;
    
    // Note: Don't set legend.display globally as it should be configurable per chart

    // Only configure datalabels if the plugin is registered
    if (Chart.defaults.plugins.datalabels) {
        Chart.defaults.plugins.datalabels.display = false; // default is no DataLabels
        Chart.defaults.plugins.datalabels.formatter = (value, context) =>
            typeof value == 'number' ? new Intl.NumberFormat([], { maximumFractionDigits: 0 }).format(value) : value;
    } else {
        console.warn('DataLabels plugin not registered, skipping datalabels configuration');
    }

    Chart.defaults.plugins.tooltip.enabled = false;

    Chart.defaults.font.family = "roboto, \"sans-serif\"";
    Chart.defaults.font.size = window.innerWidth < 768 ? 10 : 12;

    // Use the standard Fluent UI text color that updates with theme
    Chart.defaults.color = getComputedStyle(document.documentElement).getPropertyValue('--neutral-foreground-rest');

    console.log('Chart.js defaults configured');
}

/**
 * Update Chart.js theme colors and refresh all charts
 */
function updateChartTheme() {
    if (!window.Chart) return;

    const Chart = window.Chart;

    // Use the standard Fluent UI text color that updates with theme
    Chart.defaults.color = getComputedStyle(document.documentElement).getPropertyValue('--neutral-foreground-rest');

    // Force update all existing charts to apply new theme
    chartInstances.forEach((chart) => {
        if (chart && typeof chart.update === 'function') {
            chart.update('none'); // Update without animation for theme changes
        }
    });

    console.log('Chart.js theme updated');
}

/**
 * Deserialize chart configuration, evaluating function strings and removing $type properties
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
        // Let cloneDeepWith handle the rest, but we'll filter $type in the cloneDeep function
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
        } if (typeof val === 'object') {
            const cloned = {};
            for (const key in val) {
                if (val.hasOwnProperty(key) && key !== '$type') {
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

/**
 * Dispose of a chart instance
 * @param {HTMLCanvasElement} element - The canvas element
 */
export function disposeChart(element) {
    console.log("disposeChart called with element:", element);

    if (!element) {
        console.warn("disposeChart: element is null or undefined");
        return;
    }

    try {
        const chart = window.Chart ? window.Chart.getChart(element) : null;
        if (chart) {
            console.log("Destroying chart:", chart);
            chart.destroy();
            chartInstances.delete(element.id || element);
            console.log("Chart disposed successfully");
        } else {
            console.log("No chart found for element");
        }
        
        // Clean up resize listener
        if (element._resizeHandler) {
            window.removeEventListener('resize', element._resizeHandler);
            delete element._resizeHandler;
        }
    } catch (error) {
        console.error("Error disposing chart:", error);
    }
}

/**
 * Set up resize listener for responsive behavior
 * @param {Chart} chart - The Chart.js instance
 * @param {HTMLCanvasElement} element - The canvas element
 */
function setupResizeListener(chart, element) {
    let resizeTimeout;
    
    const handleResize = () => {
        clearTimeout(resizeTimeout);
        resizeTimeout = setTimeout(() => {
            if (chart && typeof chart.update === 'function') {
                const isNarrowScreen = window.innerWidth < 768;
                
                // Update responsive configurations based on screen size
                if (chart.config.options) {
                    // Update data labels for screen size
                    if (chart.config.options.plugins && chart.config.options.plugins.datalabels) {
                        if (isNarrowScreen) {
                            chart.config.options.plugins.datalabels.font = { size: 8 };
                            chart.config.options.plugins.datalabels.rotation = -90;
                            chart.config.options.plugins.datalabels.anchor = 'end';
                            chart.config.options.plugins.datalabels.align = 'top';
                        } else {
                            // Reset to default data label settings for wide screens
                            chart.config.options.plugins.datalabels.font = { size: 12 };
                            chart.config.options.plugins.datalabels.rotation = 0;
                            chart.config.options.plugins.datalabels.anchor = 'center';
                            chart.config.options.plugins.datalabels.align = 'center';
                        }
                    }
                    
                    // Update x-axis tick configuration
                    if (chart.config.options.scales && chart.config.options.scales.x && chart.config.options.scales.x.ticks) {
                        if (isNarrowScreen) {
                            chart.config.options.scales.x.ticks.maxRotation = 45;
                            chart.config.options.scales.x.ticks.callback = function(value, index, values) {
                                const label = this.getLabelForValue(value);
                                if (label && label.length > 10) {
                                    return label.substring(0, 8) + '...';
                                }
                                return label;
                            };
                        } else {
                            chart.config.options.scales.x.ticks.maxRotation = 0;
                            // Remove callback to restore full labels
                            delete chart.config.options.scales.x.ticks.callback;
                        }
                    }
                }
                
                // Force chart to resize and redraw
                chart.resize();
                chart.update('active');
            }
        }, 250); // Debounce resize events
    };
    
    // Store the resize handler so we can remove it later
    element._resizeHandler = handleResize;
    window.addEventListener('resize', handleResize);
}

/**
 * Dispose of all chart instances
 */
export function disposeAllCharts() {
    chartInstances.forEach(chart => {
        if (chart && typeof chart.destroy === 'function') {
            chart.destroy();
        }
    });
    chartInstances.clear();
}
