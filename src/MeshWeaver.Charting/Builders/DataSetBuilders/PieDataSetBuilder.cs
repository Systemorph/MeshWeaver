using System.Diagnostics.CodeAnalysis;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Segmented;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public record PieDataSetBuilder : SegmentDataSetBuilder<PieDataSetBuilder, PieDataSet>
{
    public PieDataSetBuilder InnerAlignment() => this with { DataSet = DataSet with { BorderAlign = "inner" } };
}