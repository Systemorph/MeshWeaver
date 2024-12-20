﻿using System.Diagnostics.CodeAnalysis;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public abstract record ArrayDataSetWithStyleAndOrderBuilder<TDataSetBuilder, TDataSet>
    : ArrayDataSetBuilder<TDataSetBuilder, TDataSet>, IDataSetWithPointStyleAndOrderBuilder<TDataSetBuilder, TDataSet>
    where TDataSet : DataSet, IDataSetWithPointStyle, IDataSetWithOrder, new()
    where TDataSetBuilder : ArrayDataSetWithStyleAndOrderBuilder<TDataSetBuilder, TDataSet>
{
    public TDataSetBuilder WithPointStyle(Shapes shape)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { PointStyle = shape } });

    public TDataSetBuilder InOrder(int order)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Order = order } });
}