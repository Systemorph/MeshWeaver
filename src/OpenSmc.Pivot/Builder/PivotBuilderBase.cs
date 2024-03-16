using OpenSmc.Data;
using OpenSmc.Hierarchies;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Builder.Interfaces;
using OpenSmc.Pivot.Models;
using OpenSmc.Pivot.Processors;

namespace OpenSmc.Pivot.Builder
{
    public abstract record PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> : IPivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    {
        private readonly IReadOnlyWorkspace fReadOnlyWorkspace;
        public IDimensionCache DimensionsCache { get; private init; }
        public IHierarchicalDimensionCache HierarchicalDimensionCache { get; private init; }
        public IHierarchicalDimensionOptions HierarchicalDimensionOptions { get; private init; }
        public IList<T> Objects { get; }

        public Aggregations<TTransformed, TIntermediate, TAggregate> Aggregations;
        public Func<IEnumerable<T>, IEnumerable<TTransformed>> Transformation { get; init; }
        protected Type TransposedValue { get; init; }

        protected PivotBuilderBase(IEnumerable<T> objects)
        {
            Objects = objects as IList<T> ?? objects.ToArray();
            Aggregations = new Aggregations<TTransformed, TIntermediate, TAggregate>();
            HierarchicalDimensionOptions = new HierarchicalDimensionOptions();
        }

        internal IReadOnlyWorkspace ReadOnlyWorkspace
        {
            get => fReadOnlyWorkspace;
            init
            {
                fReadOnlyWorkspace = value;
                DimensionsCache = new DimensionCache(value);
                HierarchicalDimensionCache = new HierarchicalDimensionCache(value);
            }
        }

        public TPivotBuilder WithHierarchicalDimensionOptions(Func<IHierarchicalDimensionOptions,IHierarchicalDimensionOptions> optionsFunc)
        {
            return (TPivotBuilder)this with { HierarchicalDimensionOptions = optionsFunc(HierarchicalDimensionOptions) };
        }

        public TPivotBuilder WithQuerySource(IReadOnlyWorkspace readOnlyWorkspace)
        {
            return (TPivotBuilder)this with { ReadOnlyWorkspace = readOnlyWorkspace };
        }

        public virtual TPivotBuilder Transpose<TValue>()
        {
            return (TPivotBuilder) this with { TransposedValue = typeof(TValue) };
        }


        public virtual PivotModel Execute()
        {
            var reportRenderer = GetReportProcessor();
            var ret = reportRenderer.Execute();
            return ret;
        }

        protected abstract PivotProcessorBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> GetReportProcessor();
    }
}