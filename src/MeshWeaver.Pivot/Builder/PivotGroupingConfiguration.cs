using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Models.Interfaces;
using MeshWeaver.Utils;

namespace MeshWeaver.Pivot.Builder;

public abstract record PivotGroupingConfiguration<T, TGroup>(IWorkspace Workspace)
    : IEnumerable<PivotGroupingConfigItem<T, TGroup>>
    where TGroup : class, IGroup, new()
{
    private readonly ITypeRegistry typeRegistry = Workspace.DataContext.TypeRegistry;
    internal ImmutableList<PivotGroupingConfigItem<T, TGroup>>? ConfigItems { get; init; }

    protected PivotGroupingConfigItem<T, TGroup> CreateDefaultAutomaticNumbering()
    {
        // TODO V10: this should be a new Column(){ Field = "", HeaderName = "Name", ValueGetter = "node.id"}  (2021/10/05, Ekaterina Mishina)
        if (typeof(INamed).IsAssignableFrom(typeof(T)))
        {
            return new(new(_ =>
            {
                var keyFunction = typeRegistry.GetKeyFunction(typeof(T))?.Function;
                if (keyFunction == null)
                    throw new InvalidOperationException($"No key function found for type {typeof(T)}");

                var grouper = Activator.CreateInstance(
                    typeof(NamedPivotGrouper<,>).MakeGenericType(typeof(T), typeof(TGroup)),
                    PivotConst.AutomaticEnumerationPivotGrouperName,
                    keyFunction
                );
                return (IPivotGrouper<T, TGroup>)grouper!;
            }));
        }

        return new(new(_ => new AutomaticEnumerationPivotGrouper<T, TGroup>()));
    }


    public PivotGroupingConfiguration<T, TGroup> GroupBy<TSelected>(
        Expression<Func<T, TSelected>> selector,
        IHierarchicalDimensionOptions hierarchicalDimensionOptions
    )
    {
        var reportRowGroupConfig = CreateReportGroupConfig(
            selector,
            hierarchicalDimensionOptions
        );
        return this with { ConfigItems = PrependGrouping(reportRowGroupConfig) };
    }

    private PivotGroupingConfigItem<T, TGroup> CreateReportGroupConfig<TSelected>(
        Expression<Func<T, TSelected>> selector,
        IHierarchicalDimensionOptions hierarchicalDimensionOptions
    )
    {
        return new(new(dimensionCache =>
            PivotGroupingExtensions<TGroup>.GetPivotGrouper(
                dimensionCache,
                hierarchicalDimensionOptions,
                selector
            ))
        );
    }

    protected ImmutableList<PivotGroupingConfigItem<T, TGroup>> PrependGrouping(
        PivotGroupingConfigItem<T, TGroup> configItem
    )
    {
        return ConfigItems?.Insert(0, configItem) ?? ImmutableList.Create(configItem);
    }

    protected PivotGroupingConfigItem<T, TGroup> Default { get; init; } = null!;

    public IEnumerator<PivotGroupingConfigItem<T, TGroup>> GetEnumerator()
    {
        return ConfigItems?.GetEnumerator() ?? Default.RepeatOnce().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    internal abstract PivotGroupingConfiguration<T, TGroup> Transpose();

}

public record PivotRowsGroupingConfiguration<T> : PivotGroupingConfiguration<T, RowGroup>
{
    private static readonly PivotGroupingConfigItem<T, RowGroup> DefaultTransposedRowGrouping =
        new(new(_ =>
                new DirectPivotGrouper<T, RowGroup>(
                    x => x.GroupBy(_ => IPivotGrouper<T, RowGroup>.TopGroup),
                    IPivotGrouper<T, RowGroup>.TopGroup.GrouperName!
                )
            )
        );

    public PivotRowsGroupingConfiguration(IWorkspace Workspace) : base(Workspace)
    {
        Default = CreateDefaultAutomaticNumbering();
    }

    internal override PivotGroupingConfiguration<T, RowGroup> Transpose()
    {
        return this with { Default = DefaultTransposedRowGrouping };
    }
}

public record PivotColumnsGroupingConfiguration<T> : PivotGroupingConfiguration<T, ColumnGroup>
{
    private static readonly PivotGroupingConfigItem<T, ColumnGroup> DefaultColumnGrouping =
        new(new(_ =>
                new DirectPivotGrouper<T, ColumnGroup>(
                    x => x.GroupBy(_ => IPivotGrouper<T, ColumnGroup>.TopGroup),
                    IPivotGrouper<T, ColumnGroup>.TopGroup.GrouperName!
                )
            )
        );
    private PivotGroupingConfigItem<T, ColumnGroup> DefaultTransposedGrouping =>
        CreateDefaultAutomaticNumbering();

    public PivotColumnsGroupingConfiguration(IWorkspace workspace) : base(workspace)
    {
        Default = DefaultColumnGrouping;
    }

    internal override PivotGroupingConfiguration<T, ColumnGroup> Transpose()
    {
        return this with { Default = DefaultTransposedGrouping };
    }
}
