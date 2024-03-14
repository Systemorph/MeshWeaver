using System.Diagnostics.CodeAnalysis;
using Systemorph.Charting.Models;

namespace OpenSmc.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public record PieDataSetBuilder : SegmentDataSetBuilder<PieDataSetBuilder, PieDataSet>
{
    public PieDataSetBuilder InnerAlignment() => this with { DataSet = DataSet with { BorderAlign = "inner" } };
}