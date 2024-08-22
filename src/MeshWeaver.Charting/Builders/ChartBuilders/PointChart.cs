using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record PointChart : Chart<PointChart, LineScatterDataSet>
{
    public PointChart(IReadOnlyCollection<LineScatterDataSet> dataSets)
        : base(dataSets, ChartType.Scatter) { }

    public override PointChart WithLabels(IReadOnlyCollection<string> names)
        => DataSets.Count <= names.Count
            ? base.WithLabels(names)
            : throw new Exception("Provided fewer labels than data sets");

    public override Chart ToChart()
    {
        var chart = base.ToChart();

        if (chart.Data.Labels is not null)
        {
            var labels = chart.Data.Labels.ToArray();
            chart = chart with
            {
                Data = chart.Data with
                {
                    DataSets = chart.Data.DataSets.Select((ds, i) => ds with { Label = labels[i], }).ToList(),
                    Labels = default,
                },
            };
        }

        return chart;
    }
}
