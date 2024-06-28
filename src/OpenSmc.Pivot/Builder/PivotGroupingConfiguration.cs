using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Collections;
using OpenSmc.Data;
using OpenSmc.Domain;
using OpenSmc.Hierarchies;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Builder;

public abstract record PivotGroupingConfiguration<T, TGroup>(WorkspaceState State)
    : IEnumerable<PivotGroupingConfigItem<T, TGroup>>
    where TGroup : class, IGroup, new()
{
    private readonly ITypeRegistry typeRegistry = State.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
    internal ImmutableList<PivotGroupingConfigItem<T, TGroup>> ConfigItems { get; init; }

    protected PivotGroupingConfigItem<T, TGroup> CreateDefaultAutomaticNumbering()
    {
        // TODO V10: this should be a new Column(){ Field = "", HeaderName = "Name", ValueGetter = "node.id"}  (2021/10/05, Ekaterina Mishina)
        if (typeof(INamed).IsAssignableFrom(typeof(T)))
        {
            var keyFunction = typeRegistry.GetKeyFunction(typeof(T)).Function;
            var grouper = Activator.CreateInstance(
                typeof(NamedPivotGrouper<,>).MakeGenericType(typeof(T), typeof(TGroup)),
                PivotConst.AutomaticEnumerationPivotGrouperName,
                keyFunction
            );
            return new((IPivotGrouper<T, TGroup>)grouper);
        }

        return new(new AutomaticEnumerationPivotGrouper<T, TGroup>());
    }


    public PivotGroupingConfiguration<T, TGroup> GroupBy<TSelected>(
        WorkspaceState state,
        Expression<Func<T, TSelected>> selector,
        IHierarchicalDimensionCache hierarchicalDimensionCache,
        IHierarchicalDimensionOptions hierarchicalDimensionOptions
    )
    {
        var reportRowGroupConfig = CreateReportGroupConfig(
            state,
            selector,
            hierarchicalDimensionCache,
            hierarchicalDimensionOptions
        );
        return this with { ConfigItems = PrependGrouping(reportRowGroupConfig) };
    }

    private PivotGroupingConfigItem<T, TGroup> CreateReportGroupConfig<TSelected>(
        WorkspaceState state,
        Expression<Func<T, TSelected>> selector,
        IHierarchicalDimensionCache hierarchicalDimensionCache,
        IHierarchicalDimensionOptions hierarchicalDimensionOptions
    )
    {
        return new(
            PivotGroupingExtensions<TGroup>.GetPivotGrouper(
                state,
                hierarchicalDimensionCache,
                hierarchicalDimensionOptions,
                selector
            )
        );
    }

    protected ImmutableList<PivotGroupingConfigItem<T, TGroup>> PrependGrouping(
        PivotGroupingConfigItem<T, TGroup> configItem
    )
    {
        return ConfigItems?.Insert(0, configItem) ?? ImmutableList.Create(configItem);
    }

    protected PivotGroupingConfigItem<T, TGroup> Default { get; init; }

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
        new(
            new DirectPivotGrouper<T, RowGroup>(
                x => x.GroupBy(_ => IPivotGrouper<T, RowGroup>.TopGroup),
                IPivotGrouper<T, RowGroup>.TopGroup.GrouperName
            )
        );

    public PivotRowsGroupingConfiguration(WorkspaceState State) : base(State)
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
        new(
            new DirectPivotGrouper<T, ColumnGroup>(
                x => x.GroupBy(_ => IPivotGrouper<T, ColumnGroup>.TopGroup),
                IPivotGrouper<T, ColumnGroup>.TopGroup.GrouperName
            )
        );
    private PivotGroupingConfigItem<T, ColumnGroup> DefaultTransposedGrouping =>
        CreateDefaultAutomaticNumbering();

    public PivotColumnsGroupingConfiguration(WorkspaceState state) : base(state)
    {
        Default = DefaultColumnGrouping;
    }

    internal override PivotGroupingConfiguration<T, ColumnGroup> Transpose()
    {
        return this with { Default = DefaultTransposedGrouping };
    }
}
