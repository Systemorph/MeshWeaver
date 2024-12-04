using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Line;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public record TimeLineDataSetBuilder : LineDataSetBuilderBase<TimeLineDataSetBuilder, TimeLineDataSet>
{
    public TimeLineDataSetBuilder WithData(IEnumerable<DateTime> dates, IEnumerable<int> rawData) => WithData(dates, rawData.Select(x => (double)x));
    public TimeLineDataSetBuilder WithData(IEnumerable<string> times, IEnumerable<double> rawData) => WithData(times.Select(DateTime.Parse), rawData);
    public TimeLineDataSetBuilder WithData(IEnumerable<string> times, IEnumerable<int> rawData) => WithData(times.Select(DateTime.Parse), rawData);
    public TimeLineDataSetBuilder WithData(IEnumerable<DateTime> dates, IEnumerable<double> rawData)
    {
        var datesList = dates.ToList();
        var rawDataList = rawData.ToList();
        if (rawDataList.Count != datesList.Count)
            throw new ArgumentException($"'{nameof(dates)}' and '{nameof(rawData)}' arrays MUST have the same length");

        var data = datesList.Select((t, index) => new TimePointData { X = t.ToString("o", CultureInfo.InvariantCulture), Y = rawDataList[index] });

        var dataSet = new TimeLineDataSet { Data = data };
        return this with { DataSet = dataSet };
    }

    public override TimeLineDataSetBuilder WithArea() => this with { DataSet = DataSet with { Fill = "origin" } };
}