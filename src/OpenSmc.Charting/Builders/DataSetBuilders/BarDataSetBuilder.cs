using System.Diagnostics.CodeAnalysis;
using Systemorph.Charting.Models;

namespace OpenSmc.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public record BarDataSetBuilder : BarDataSetBuilderBase<BarDataSetBuilder, BarDataSet>;