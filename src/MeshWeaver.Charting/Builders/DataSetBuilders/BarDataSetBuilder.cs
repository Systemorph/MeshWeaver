using System.Diagnostics.CodeAnalysis;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public record BarDataSetBuilder : BarDataSetBuilderBase<BarDataSetBuilder, BarDataSet>;