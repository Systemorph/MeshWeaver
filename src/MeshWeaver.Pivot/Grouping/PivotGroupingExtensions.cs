using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models.Interfaces;
using MeshWeaver.Reflection;

// ReSharper disable StaticMemberInGenericType

namespace MeshWeaver.Pivot.Grouping
{
    internal static class PivotGroupingExtensions<TGroup>
        where TGroup : class, IGroup, new()
    {
        public static IPivotGrouper<TTransformed, TGroup> GetPivotGrouper<TTransformed, TSelected>(
            WorkspaceState state,
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions,
            Expression<Func<TTransformed, TSelected>> selector
        )
        {
            var compiledSelector = selector.Compile();

            // TODO V10: do we want to support this selector? (2022/03/10, Ekaterina Mishina)
            if (typeof(TGroup).IsAssignableFrom(typeof(TSelected)))
            {
                string grouperName = GetGrouperName(selector);
                return new DirectPivotGrouper<TTransformed, TGroup>(
                    x => x.GroupBy(y => (TGroup)(object)compiledSelector(y)),
                    grouperName
                );
            }

            var property = GetProperty(selector);
            var dimensionAttribute = property?.GetCustomAttribute<DimensionAttribute>();
            if (dimensionAttribute != null)
            {
                var dimensionDescriptor = new DimensionDescriptor(
                    dimensionAttribute.Name,
                    dimensionAttribute.Type
                );
                return GetPivotGrouper(
                    state,
                    hierarchicalDimensionCache,
                    hierarchicalDimensionOptions,
                    dimensionDescriptor,
                    compiledSelector
                );
            }

            // TODO V10: find use cases (2022/03/08, Ekaterina Mishina)
            return new SelectorPivotGrouper<TTransformed, TSelected, TGroup>(
                compiledSelector,
                property?.Name ?? PivotConst.PropertyPivotGrouperName
            );
        }

        private class GrouperNameVisitor : ExpressionVisitor
        {
            public string GrouperName;

            protected override Expression VisitNew(NewExpression node)
            {
                if (node.Type == typeof(TGroup))
                {
                    //node.Constructor.GetParameters().FirstOrDefault(x => x.Name == "grouperName");
                    var grouperNameVariable = node
                        .Constructor?.GetParameters()
                        .Zip(node.Arguments, (p, a) => (p, a))
                        .Where(x => x.p.Name == "grouperName")
                        .Select(x => x.a)
                        .FirstOrDefault();

                    if (grouperNameVariable is ConstantExpression c)
                        GrouperName = (string)c.Value;
                }
                return base.VisitNew(node);
            }
        }

        private static string GetGrouperName<TTransformed, TSelected>(
            Expression<Func<TTransformed, TSelected>> selector
        )
        {
            var grouperNameVisitor = new GrouperNameVisitor();
            grouperNameVisitor.Visit(selector);
            return grouperNameVisitor.GrouperName;
        }

        public static IPivotGrouper<DataSlice<TElement>, TGroup> GetPivotGrouper<TElement>(
            DimensionDescriptor descriptor,
            WorkspaceState state,
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions
        )
        {
            if (typeof(INamed).IsAssignableFrom(descriptor.Type))
                return GetPivotGrouper<DataSlice<TElement>, object>(
                    state,
                    hierarchicalDimensionCache,
                    hierarchicalDimensionOptions,
                    descriptor,
                    x => x.Tuple.GetValue(descriptor.SystemName)
                );

            var convertedLambda = GetValueMethod
                .MakeGenericMethod(typeof(TElement), descriptor.Type)
                .InvokeAsFunction(descriptor.SystemName);
            return (IPivotGrouper<DataSlice<TElement>, TGroup>)
                GetReportGrouperMethod
                    .MakeGenericMethod(typeof(DataSlice<TElement>), descriptor.Type)
                    .Invoke(
                        null,
                        new[]
                        {
                            state,
                            hierarchicalDimensionCache,
                            hierarchicalDimensionOptions,
                            descriptor,
                            convertedLambda
                        }
                    );
        }

        private static readonly MethodInfo GetValueMethod = ReflectionHelper.GetStaticMethodGeneric(
            () => GetValue<object, object>(null)
        );

        // ReSharper disable once UnusedMethodReturnValue.Local
        private static Func<DataSlice<TElement>, TSelected> GetValue<TElement, TSelected>(
            string dim
        )
        {
            return dataSlice => (TSelected)dataSlice.Tuple.GetValue(dim);
        }

        private static readonly MethodInfo GetReportGrouperMethod =
            ReflectionHelper.GetStaticMethodGeneric(
                () => GetPivotGrouper<object, object>(null, null, null, null, null)
            );

        private static IPivotGrouper<TTransformed, TGroup> GetPivotGrouper<TTransformed, TSelected>(
            WorkspaceState state,
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions,
            DimensionDescriptor descriptor,
            Func<TTransformed, TSelected> selector
        )
        {
            if (typeof(PropertyInfo) == descriptor.Type)
                return new PropertyPivotGrouper<TTransformed, TGroup>(
                    (Func<TTransformed, PropertyInfo>)(object)selector
                );

            if (typeof(INamed).IsAssignableFrom(descriptor.Type))
                return (IPivotGrouper<TTransformed, TGroup>)
                    GetDimensionReportGroupConfigMethod
                        .MakeGenericMethod(typeof(TTransformed), descriptor.Type)
                        .InvokeAsFunction(
                            state,
                            hierarchicalDimensionCache,
                            hierarchicalDimensionOptions,
                            selector,
                            new DimensionDescriptor(descriptor.SystemName, descriptor.Type)
                        );

            return new SelectorPivotGrouper<TTransformed, TSelected, TGroup>(
                selector,
                descriptor.SystemName
            );
        }

        private static readonly MethodInfo GetDimensionReportGroupConfigMethod =
            ReflectionHelper.GetStaticMethodGeneric(
                () => GetDimensionReportGroupConfig<object, INamed>(null, null, null, null, null)
            );

        // ReSharper disable once UnusedMethodReturnValue.Local
        private static IPivotGrouper<TTransformed, TGroup> GetDimensionReportGroupConfig<
            TTransformed,
            TDimension
        >(
            WorkspaceState state,
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions,
            Func<TTransformed, object> selector,
            DimensionDescriptor descriptor
        )
            where TDimension : class, INamed
        {
            if (typeof(IWithParent).IsAssignableFrom(typeof(TDimension)))
                return (IPivotGrouper<TTransformed, TGroup>)
                    CreateHierarchicalDimensionPivotGrouperMethod
                        .MakeGenericMethod(typeof(TTransformed), typeof(TDimension))
                        .InvokeAsFunction(
                            state,
                            hierarchicalDimensionCache,
                            hierarchicalDimensionOptions,
                            selector,
                            descriptor
                        );

            return new DimensionPivotGrouper<TTransformed, TDimension, TGroup>(
                state,
                selector,
                descriptor
            );
        }

        private static readonly IGenericMethodCache CreateHierarchicalDimensionPivotGrouperMethod =
            GenericCaches.GetMethodCacheStatic(
                () =>
                    CreateHierarchicalDimensionPivotGrouper<object, IHierarchicalDimension>(
                        default,
                        default,
                        default,
                        default,
                        default
                    )
            );

        [SuppressMessage("ReSharper", "UnusedMethodReturnValue.Local")]
        private static HierarchicalDimensionPivotGrouper<
            TTransformed,
            TDimension,
            TGroup
        > CreateHierarchicalDimensionPivotGrouper<TTransformed, TDimension>(
            WorkspaceState state,
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions,
            Func<TTransformed, object> selector,
            DimensionDescriptor descriptor
        )
            where TDimension : class, IHierarchicalDimension
        {
            return new(
                state,
                hierarchicalDimensionCache,
                hierarchicalDimensionOptions,
                selector,
                descriptor
            );
        }

        public static PropertyInfo GetProperty<TTransformed, TSelected>(
            Expression<Func<TTransformed, TSelected>> selector
        )
        {
            var memberExpression = selector.Body as MemberExpression;
            return memberExpression?.Member as PropertyInfo;
        }
    }
}
