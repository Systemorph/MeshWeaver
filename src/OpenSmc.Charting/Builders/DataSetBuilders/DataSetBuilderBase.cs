using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Helpers;
using OpenSmc.Charting.Models;
using OpenSmc.Charting.Models.Options;

namespace OpenSmc.Charting.Builders.DataSetBuilders;

public abstract record DataSetBuilderBase<TDataSetBuilder, TDataSet>
    where TDataSet : DataSet
    where TDataSetBuilder : DataSetBuilderBase<TDataSetBuilder, TDataSet>
{
    protected internal TDataSet DataSet { get; init; }
    public DataSet Build() => DataSet;

    public TDataSetBuilder WithLabel(string label)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Label = label } });
    
    public TDataSetBuilder WithDataLabels(DataLabels dataLabels)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { DataLabels = dataLabels } });

    public TDataSetBuilder WithLineWidth(int width) => WithBorderWidth(width);

    public TDataSetBuilder WithBorderWidth(int width)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { BorderWidth = width } });

    public TDataSetBuilder WithBorderWidth(IEnumerable<int> widths)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { BorderWidth = widths } });

    public TDataSetBuilder SetType(ChartType? type)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Type = type } });

    public TDataSetBuilder SetType(string type)
        => (TDataSetBuilder)(this with
                             {
                                 DataSet = DataSet with
                                           {
                                               Type = type switch
                                               {
                                                   "bar" => ChartType.Bar,
                                                   "bubble" => ChartType.Bubble,
                                                   "radar" => ChartType.Radar,
                                                   "polarArea" => ChartType.PolarArea,
                                                   "pie" => ChartType.Pie,
                                                   "line" => ChartType.Line,
                                                   "doughnut" => ChartType.Doughnut,
                                                   "horizontalBar" => ChartType.HorizontalBar,
                                                   "scatter" => ChartType.Scatter,
                                                   _ => throw new ArgumentException(nameof(type))
                                               }
                                           }
                             });

    public TDataSetBuilder WithBackgroundColor(IEnumerable<ChartColor> colors)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { BackgroundColor = colors } });

    public TDataSetBuilder WithBackgroundColor(ChartColor color)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { BackgroundColor = color } });

    public TDataSetBuilder WithHoverBackgroundColor(IEnumerable<ChartColor> colors)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { HoverBackgroundColor = colors } });

    public TDataSetBuilder WithHoverBackgroundColor(ChartColor color)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { HoverBackgroundColor = color } });

    public TDataSetBuilder WithParsing(Parsing parsing)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Parsing = parsing } });
}
