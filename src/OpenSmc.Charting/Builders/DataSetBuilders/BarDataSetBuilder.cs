using System.Diagnostics.CodeAnalysis;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public record BarDataSetBuilder : BarDataSetBuilderBase<BarDataSetBuilder, BarDataSet>;