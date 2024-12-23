using System.Collections;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Charting.Waterfall;

namespace MeshWeaver.Charting;

public static class Chart
{
    /// <summary>
    /// Creates a bar chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bar chart.</returns>
    public static ChartModel Bar(IEnumerable data, string label = null) => new(DataSet.Bar(data, label));

    /// <summary>
    /// Creates a doughnut chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a doughnut chart.</returns>
    public static ChartModel Doughnut(IEnumerable data, string label = null)
        => new(DataSet.Doughnut(data, label));

    /// <summary>
    /// Creates a line chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a line chart.</returns>
    public static ChartModel Line(IEnumerable data, string label = null)
        => new(DataSet.Line(data, label));
    /// <summary>
    /// Creates a pie chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a pie chart.</returns>
    public static ChartModel Pie(IEnumerable data, string label = null)
        => new(DataSet.Pie(data, label));

    /// <summary>
    /// Creates a polar area chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a polar area chart.</returns>
    public static ChartModel Polar(IEnumerable data, string label = null)
        => new(DataSet.Polar(data, label));

    /// <summary>
    /// Creates a radar chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a radar chart.</returns>
    public static ChartModel Radar(IEnumerable data, string label = null)
        => new(DataSet.Radar(data, label));

    /// <summary>
    /// Creates a floating bar chart model.
    /// </summary>
    /// <param name="dataFrom">The starting values of the data range.</param>
    /// <param name="dataTo">The ending values of the data range.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a floating bar chart.</returns>
    public static ChartModel FloatingBar(IEnumerable dataFrom, IEnumerable dataTo, string label = null) =>
        new(DataSet.FloatingBar(dataFrom, dataTo, label));

    /// <summary>
    /// Creates a horizontal floating bar chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a horizontal floating bar chart.</returns>
    public static ChartModel HorizontalFloatingBar(IEnumerable data, string label = null)
        => new(DataSet.HorizontalFloatingBar(data, label));


    /// <summary>
    /// Creates a timeline chart model.
    /// </summary>
    /// <param name="times">The times for the x-axis.</param>
    /// <param name="rawData">The data values for the y-axis.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a time line chart.</returns>
    public static ChartModel TimeLine(IEnumerable<string> times, IEnumerable<double> rawData, string label = null) => new(DataSet.TimeLine(times.Select(DateTime.Parse), rawData, label));


    /// <summary>
    /// Creates a timeline chart model.
    /// </summary>
    /// <param name="dates">The dates for the x-axis.</param>
    /// <param name="rawData">The data values for the y-axis.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a timeline chart.</returns>
    public static ChartModel TimeLine(IEnumerable<DateTime> dates, IEnumerable<double> rawData, string label = null)
    => new(DataSet.TimeLine(dates, rawData, label));

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<double> x, IEnumerable<int> y, string label = null) => Scatter(x, y.Select(v => (double)v), label);

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<int> x, IEnumerable<double> y, string label = null) => Scatter(x.Select(v => (double)v), y, label);

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<int> x, IEnumerable<int> y, string label = null) => Scatter(x.Select(v => (double)v), y.Select(v => (double)v), label);

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<double> x, IEnumerable<double> y, string label = null)
        => new(DataSet.Scatter(x, y, label));

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="points">The points to plot, each represented as a tuple of (x, y).</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<(int x, int y)> points, string label = null)
        => new(DataSet.Scatter(points, label));

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="points">The points to plot, each represented as a tuple of (x, y).</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<(int x, double y)> points, string label = null)
        => new(DataSet.Scatter(points, label));

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="points">The points to plot, each represented as a tuple of (x, y).</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<(double x, int y)> points, string label = null)
        => new(DataSet.Scatter(points, label));

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="points">The points to plot, each represented as a tuple of (x, y).</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<(double x, double y)> points, string label = null)
        => new(DataSet.Scatter(points, label));


    public static ChartModel ToChart(this IEnumerable<DataSet> dataSets) => Create(dataSets.ToArray());

    public static ChartModel Create(params DataSet[] dataSets) =>
        dataSets
            .OfType<IChartOptionsConfiguration>()
            .Aggregate(new ChartModel(dataSets), (r, c) => r.WithOptions(c.Configure));




    public static ChartModel Waterfall(List<double> deltas,
        Func<WaterfallChartOptions, WaterfallChartOptions> options = null
    )
        => new ChartModel()
            .ToWaterfallChart(deltas, options)
            .WithOptions(o => o
                .Stacked("x")
                .HideAxis("y")
                .HideGrid("x")
            );

    public static ChartModel HorizontalWaterfall(List<double> deltas,
        Func<HorizontalWaterfallChartOptions, HorizontalWaterfallChartOptions> options = null
    )
        => new ChartModel()
            .ToWaterfallChart(deltas, options)
            .WithOptions(o => o
                .Stacked("y")
                //.HideAxis("x")
                .Grace<CartesianLinearScale>("x", "10%")
                // TODO V10: understand why the line below helps and find less random approach (2023/10/08, Ekaterina Mishina)
                .SuggestedMax("x", 10) // this helps in case of all negative values
                .ShortenAxisNumbers("x")
                .WithIndexAxis("y")
            );
    #region multiple enumerables
    /// <summary>
    /// Creates a bar chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bar chart.</returns>
    public static ChartModel Bar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.Bar(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a doughnut chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a doughnut chart.</returns>
    public static ChartModel Doughnut(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.Doughnut(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a line chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a line chart.</returns>
    public static ChartModel Line(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.Line(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a pie chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a pie chart.</returns>
    public static ChartModel Pie(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.Pie(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a polar area chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a polar area chart.</returns>
    public static ChartModel Polar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.Polar(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a radar chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a radar chart.</returns>
    public static ChartModel Radar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.Radar(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a floating bar chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a floating bar chart.</returns>
    public static ChartModel FloatingBar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.FloatingBar(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a horizontal floating bar chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a horizontal floating bar chart.</returns>
    public static ChartModel HorizontalFloatingBar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.HorizontalFloatingBar(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a bubble chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bubble chart.</returns>
    public static ChartModel Bubble(IEnumerable<BubbleData> data, string label = null) => Create(DataSet.Bubble(data.ToArray(), label));
    /// <summary>
    /// Creates a new instance of <see cref="ChartModel"/> from tuples.
    /// </summary>
    /// <param name="values">The tuples representing the data points.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/>.</returns>
    public static ChartModel Bubble(IEnumerable<(double x, double y, double radius)> values, string label = null) => new(DataSet.Bubble(values, label));

    /// <summary>
    /// Creates a new instance of <see cref="ChartModel"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="radius">The radius values.</param>
    /// <param name="label"></param>
    /// <returns>A new instance of <see cref="ChartModel"/>.</returns>
    public static ChartModel Bubble(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius, string label = null) => new(DataSet.Bubble(x, y, radius, label));



    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => DataSet.Scatter(d.Cast<object>().ToArray())));
    #endregion

}
