﻿using System.Collections;
using System.Globalization;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Bubble;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Charting.Models.Polar;
using MeshWeaver.Charting.Models.Radar;
using MeshWeaver.Charting.Models.Segmented;
using MeshWeaver.Charting.Waterfall;

namespace MeshWeaver.Charting;

public static class Chart
{

    /// <summary>
    /// Creates a bar chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bar chart.</returns>
    public static ChartModel Bar(IEnumerable data)
    {
        var dataArray = ToArrayIfNotEmpty(data);
        // Refactored to use the Bar method with BarDataSet
        return dataArray == null ? null : ToChart(new BarDataSet(dataArray));
    }

    /// <summary>
    /// Creates a doughnut chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a doughnut chart.</returns>
    public static ChartModel Doughnut(IEnumerable data)
    {
        var dataArray = ToArrayIfNotEmpty(data);
        // Refactored to use the Doughnut method with DoughnutDataSet
        return dataArray == null ? null : ToChart(new DoughnutDataSet(dataArray));
    }

    /// <summary>
    /// Creates a line chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a line chart.</returns>
    public static ChartModel Line(IEnumerable data)
    {
        var dataArray = ToArrayIfNotEmpty(data);
        // Refactored to use the Line method with LineDataSet
        return dataArray == null ? null : ToChart(new LineDataSet(dataArray));
    }

    /// <summary>
    /// Creates a pie chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a pie chart.</returns>
    public static ChartModel Pie(IEnumerable data)
    {
        var dataArray = ToArrayIfNotEmpty(data);
        // Refactored to use the Pie method with PieDataSet
        return dataArray == null ? null : ToChart(new PieDataSet(dataArray));
    }

    /// <summary>
    /// Creates a polar area chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a polar area chart.</returns>
    public static ChartModel Polar(IEnumerable data)
    {
        var dataArray = ToArrayIfNotEmpty(data);
        // Refactored to use the Polar method with PolarDataSet
        return dataArray == null ? null : ToChart(new PolarDataSet(dataArray));
    }

    /// <summary>
    /// Creates a radar chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a radar chart.</returns>
    public static ChartModel Radar(IEnumerable data)
    {
        var dataArray = ToArrayIfNotEmpty(data);
        // Refactored to use the Radar method with RadarDataSet
        return dataArray == null ? null : ToChart(new RadarDataSet(dataArray));
    }

    /// <summary>
    /// Creates a floating bar chart model.
    /// </summary>
    /// <param name="dataFrom">The starting values of the data range.</param>
    /// <param name="dataTo">The ending values of the data range.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a floating bar chart.</returns>
    public static ChartModel FloatingBar(IEnumerable dataFrom, IEnumerable dataTo)
    {
        var dataFromArray = ToArrayIfNotEmpty(dataFrom);
        var dataToArray = ToArrayIfNotEmpty(dataTo);
        if (dataFromArray == null || dataToArray == null) return null;

        var rangeData = dataFromArray.Zip(dataToArray, (from, to) => new[] { from, to });
        // Refactored to use the FloatingBar method with FloatingBarDataSet
        return ToChart(new FloatingBarDataSet(rangeData.Cast<object>().ToArray()));
    }

    /// <summary>
    /// Creates a horizontal floating bar chart model.
    /// </summary>
    /// <param name="data">The data to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a horizontal floating bar chart.</returns>
    public static ChartModel HorizontalFloatingBar(IEnumerable data)
    {
        var dataArray = ToArrayIfNotEmpty(data);
        // Refactored to use the HorizontalFloatingBar method with HorizontalFloatingBarDataSet
        return dataArray == null ? null : ToChart(new HorizontalFloatingBarDataSet(dataArray));
    }

    /// <summary>
    /// Creates a bubble chart model.
    /// </summary>
    /// <param name="values">The values to plot, each represented as a tuple of (x, y, radius).</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bubble chart.</returns>
    public static ChartModel Bubble(IEnumerable<(int x, int y, int radius)> values)
    {
        var valuesArray = ToArrayIfNotEmpty(values);
        if (valuesArray == null) return null;

        var valueTuples = valuesArray.ToList();
        return Bubble(valueTuples.Select(e => (double)e.x), valueTuples.Select(e => (double)e.y), valueTuples.Select(e => (double)e.radius));
    }

    /// <summary>
    /// Creates a bubble chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="radius">The radius values.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bubble chart.</returns>
    public static ChartModel Bubble(IEnumerable<int> x, IEnumerable<int> y, IEnumerable<double> radius)
        => Bubble(x.Select(e => (double)e), y.Select(e => (double)e), radius);

    /// <summary>
    /// Creates a bubble chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="radius">The radius values.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bubble chart.</returns>
    public static ChartModel Bubble(IEnumerable<double> x, IEnumerable<int> y, IEnumerable<double> radius)
        => Bubble(x.Select(e => e), y.Select(e => (double)e), radius);

    /// <summary>
    /// Creates a bubble chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="radius">The radius values.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bubble chart.</returns>
    public static ChartModel Bubble(IEnumerable<int> x, IEnumerable<double> y, IEnumerable<double> radius)
        => Bubble(x.Select(e => (double)e), y.Select(e => e), radius);

    /// <summary>
    /// Creates a bubble chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="radius">The radius values.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bubble chart.</returns>
    public static ChartModel Bubble(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius)
    {
        var xArray = ToArrayIfNotEmpty(x);
        var yArray = ToArrayIfNotEmpty(y);
        var radiusArray = ToArrayIfNotEmpty(radius);
        if (xArray == null || yArray == null || radiusArray == null) return null;

        if (xArray.Count != yArray.Count || xArray.Count != radiusArray.Count)
            throw new InvalidOperationException();

        var pointData = Enumerable.Range(0, xArray.Count)
            .Select(i => new BubbleData(xArray[i], yArray[i], radiusArray[i]))
            .ToArray();

        // Refactored to use the Bubble method with BubbleDataSet
        return ToChart(new BubbleDataSet(pointData));
    }

    /// <summary>
    /// Creates a time line chart model.
    /// </summary>
    /// <param name="dates">The dates for the x-axis.</param>
    /// <param name="rawData">The data values for the y-axis.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a time line chart.</returns>
    public static ChartModel TimeLine(IEnumerable<DateTime> dates, IEnumerable<int> rawData) => TimeLine(dates, rawData.Select(x => (double)x));

    /// <summary>
    /// Creates a time line chart model.
    /// </summary>
    /// <param name="times">The times for the x-axis.</param>
    /// <param name="rawData">The data values for the y-axis.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a time line chart.</returns>
    public static ChartModel TimeLine(IEnumerable<string> times, IEnumerable<double> rawData) => TimeLine(times.Select(DateTime.Parse), rawData);

    /// <summary>
    /// Creates a time line chart model.
    /// </summary>
    /// <param name="times">The times for the x-axis.</param>
    /// <param name="rawData">The data values for the y-axis.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a time line chart.</returns>
    public static ChartModel TimeLine(IEnumerable<string> times, IEnumerable<int> rawData) => TimeLine(times.Select(DateTime.Parse), rawData);

    /// <summary>
    /// Creates a time line chart model.
    /// </summary>
    /// <param name="dates">The dates for the x-axis.</param>
    /// <param name="rawData">The data values for the y-axis.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a time line chart.</returns>
    public static ChartModel TimeLine(IEnumerable<DateTime> dates, IEnumerable<double> rawData)
    {
        var datesArray = ToArrayIfNotEmpty(dates);
        var rawDataArray = ToArrayIfNotEmpty(rawData);
        if (datesArray == null || rawDataArray == null) return null;

        if (rawDataArray.Count != datesArray.Count)
            throw new ArgumentException($"'{nameof(dates)}' and '{nameof(rawData)}' arrays MUST have the same length");

        var data = datesArray
            .Select((t, index) => new TimePointData { X = t.ToString("o", CultureInfo.InvariantCulture), Y = rawDataArray[index] })
            .Cast<object>()
            .ToArray();

        // Refactored to use the TimeLine method with TimeLineDataSet
        return ToChart(new TimeLineDataSet(data));
    }

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<double> x, IEnumerable<int> y) => Scatter(x, y.Select(v => (double)v));

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<int> x, IEnumerable<double> y) => Scatter(x.Select(v => (double)v), y);

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<int> x, IEnumerable<int> y) => Scatter(x.Select(v => (double)v), y.Select(v => (double)v));

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<double> x, IEnumerable<double> y)
    {
        var xArray = ToArrayIfNotEmpty(x);
        var yArray = ToArrayIfNotEmpty(y);
        if (xArray == null || yArray == null) return null;

        if (xArray.Count != yArray.Count)
            throw new InvalidOperationException();

        var pointData = xArray.Zip(yArray, (a, v) => new PointData { X = a, Y = v }).Cast<object>().ToArray();

        // Refactored to use the Scatter method with LineScatterDataSet
        return ToChart(new LineScatterDataSet(pointData));
    }

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="points">The points to plot, each represented as a tuple of (x, y).</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<(int x, int y)> points)
    {
        var pointsArray = ToArrayIfNotEmpty(points);
        return pointsArray == null ? null : Scatter(pointsArray.Select(p => ((double)p.x, (double)p.y)));
    }

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="points">The points to plot, each represented as a tuple of (x, y).</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<(int x, double y)> points)
    {
        var pointsArray = ToArrayIfNotEmpty(points);
        return pointsArray == null ? null : Scatter(pointsArray.Select(p => ((double)p.x, p.y)));
    }

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="points">The points to plot, each represented as a tuple of (x, y).</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<(double x, int y)> points)
    {
        var pointsArray = ToArrayIfNotEmpty(points);
        return pointsArray == null ? null : Scatter(pointsArray.Select(p => (p.x, (double)p.y)));
    }

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="points">The points to plot, each represented as a tuple of (x, y).</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(IEnumerable<(double x, double y)> points)
    {
        var pointsArray = ToArrayIfNotEmpty(points);
        // Refactored to use the Scatter method with LineScatterDataSet
        return pointsArray == null ? null : ToChart(new LineScatterDataSet(pointsArray.Select(p => new PointData { X = p.x, Y = p.y }).Cast<object>().ToArray()));
    }

    /// <summary>
    /// Checks if the data is null or empty after converting to an array.
    /// </summary>
    /// <param name="data">The data to check.</param>
    /// <returns>The data as an array if it is not null or empty; otherwise, null.</returns>
    private static IReadOnlyCollection<object> ToArrayIfNotEmpty(IEnumerable data)
    {
        var dataArray = data?.Cast<object>().ToArray();
        return dataArray != null && dataArray.Any() ? dataArray : null;
    }


    public static ChartModel ToChart(this IEnumerable<DataSet> dataSets) => ToChart(dataSets.ToArray());

    public static ChartModel ToChart(params DataSet[] dataSets) => 
        dataSets
            .OfType<IChartOptionsConfiguration>()
            .Aggregate(new ChartModel(dataSets), (r, c) => r.WithOptions(c.Configure));


    /// <summary>
    /// Checks if the data is null or empty after converting to an array.
    /// </summary>
    /// <typeparam name="T">The type of the data.</typeparam>
    /// <param name="data">The data to check.</param>
    /// <returns>The data as an array if it is not null or empty; otherwise, null.</returns>
    private static IReadOnlyList<T> ToArrayIfNotEmpty<T>(IEnumerable<T> data)
    {
        var dataArray = data?.ToArray();
        return dataArray != null && dataArray.Any() ? dataArray : null;
    }


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
    public static ChartModel Bar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new BarDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a doughnut chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a doughnut chart.</returns>
    public static ChartModel Doughnut(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new DoughnutDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a line chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a line chart.</returns>
    public static ChartModel Line(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new LineDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a pie chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a pie chart.</returns>
    public static ChartModel Pie(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new PieDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a polar area chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a polar area chart.</returns>
    public static ChartModel Polar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new PolarDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a radar chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a radar chart.</returns>
    public static ChartModel Radar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new RadarDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a floating bar chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a floating bar chart.</returns>
    public static ChartModel FloatingBar(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new FloatingBarDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a horizontal floating bar chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a horizontal floating bar chart.</returns>
    public static ChartModel HorizontalFloatingBar(params IEnumerable<IEnumerable> data) => HorizontalFloatingBar(data.Select(d => new HorizontalFloatingBarDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a bubble chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a bubble chart.</returns>
    public static ChartModel Bubble(params IEnumerable<BubbleData> data) => ToChart(new BubbleDataSet(data.ToArray()));

    /// <summary>
    /// Creates a time line chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a time line chart.</returns>
    public static ChartModel TimeLine(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new TimeLineDataSet(d.Cast<object>().ToArray())));

    /// <summary>
    /// Creates a scatter chart model.
    /// </summary>
    /// <param name="data">The data sets to plot.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a scatter chart.</returns>
    public static ChartModel Scatter(params IEnumerable<IEnumerable> data) => ToChart(data.Select(d => new LineScatterDataSet(d.Cast<object>().ToArray())));

    #endregion

}