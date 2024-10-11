using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder.Interfaces;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Processors;

namespace MeshWeaver.Pivot.Builder
{
    public abstract record PivotBuilderBase<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    > : IPivotBuilder, IPivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        public IHierarchicalDimensionOptions HierarchicalDimensionOptions { get; private init; }
        public IList<T> Objects { get; }

        public Aggregations<TTransformed, TIntermediate, TAggregate> Aggregations;
        public Func<IEnumerable<T>, IEnumerable<TTransformed>> Transformation { get; init; }
        protected Type TransposedValue { get; init; }
        public IWorkspace Workspace { get; init; }
        protected PivotBuilderBase(IWorkspace workspace, IEnumerable<T> objects)
        {
            Objects = objects as IList<T> ?? objects.ToArray();
            Aggregations = new Aggregations<TTransformed, TIntermediate, TAggregate>();
            HierarchicalDimensionOptions = new HierarchicalDimensionOptions();
            Workspace = workspace;
            var types = Objects.Select(o => o.GetType()).Distinct().ToArray();
            var dimensions = types
                .SelectMany(t => t.GetProperties().Select(p => p.GetCustomAttribute<DimensionAttribute>()?.Type))
                .Where(x => x != null)
                .ToArray();
            var reference = dimensions.Select(Workspace.DataContext.TypeRegistry.GetCollectionName).Where(x => x != null).ToArray();
            var stream = reference.Any()
                ? ((IObservable<ChangeItem<EntityStore>>)Workspace.Stream.Reduce(new CollectionsReference(reference))).Select(x => x.Value)
                : Observable.Return<EntityStore>(new());
        }

        public TPivotBuilder WithHierarchicalDimensionOptions(
            Func<IHierarchicalDimensionOptions, IHierarchicalDimensionOptions> optionsFunc
        )
        {
            return (TPivotBuilder)this with
            {
                HierarchicalDimensionOptions = optionsFunc(HierarchicalDimensionOptions)
            };
        }

        public virtual TPivotBuilder Transpose<TValue>()
        {
            return (TPivotBuilder)this with { TransposedValue = typeof(TValue) };
        }

        public virtual PivotModel Execute()
        {
            var reportRenderer = GetReportProcessor();
            var ret = reportRenderer.Execute();
            return ret;
        }

        protected abstract PivotProcessorBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        > GetReportProcessor();
    }
}
