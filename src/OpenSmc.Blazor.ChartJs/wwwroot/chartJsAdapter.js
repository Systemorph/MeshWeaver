// This is a JavaScript module that is loaded on demand. It can export any number of
// functions, and may import other JavaScript modules if required.


window.renderChart = function (chartId, chartData) {
    var ctx = document.getElementById(chartId).getContext('2d');
    new Chart(ctx, chartData);
};
//export function renderChart(chartId, chartData) {
//        console.log(chartId);
//        console.log(chartData);
//        var ctx = document.getElementById(chartId).getContext('2d');
//        new Chart(ctx, chartData);
//}



