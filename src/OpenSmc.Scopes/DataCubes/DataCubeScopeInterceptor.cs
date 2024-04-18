using System.Collections;
using System.Reflection;
using AspectCore.Extensions.Reflection;
using Castle.DynamicProxy;
using OpenSmc.Collections;
using OpenSmc.DataCubes;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Reflection;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.DataCubes
{
    public class DataCubeScopeInterceptor<TScope, TElement, TIdentity> : ScopeInterceptorBase
        where TElement : class
    {
        private static readonly (PropertyInfo Property, PropertyReflector Reflector)[] DataElementProperties = typeof(TScope).GetProperties()
                                                                                                                             .Where(x => !x.HasAttribute<NotVisibleAttribute>())
                                                                                                                             .Where(p => typeof(TElement).IsAssignableFrom(p.PropertyType))
                                                                                                                             .Select(p => (p, p.GetReflector())).ToArray();

        private static readonly (PropertyInfo Property, PropertyReflector Reflector)[] DataCubeProperties = typeof(TScope).GetProperties()
                                                                                                                          .Where(x => !x.HasAttribute<NotVisibleAttribute>())
                                                                                                                          .Where(p => p.PropertyType.GetDataCubeElementType() == typeof(TElement))
                                                                                                                          .Select(p => (p, p.GetReflector())).ToArray();
        //private readonly Type scopeType;
        //private readonly PropertyReflector[] dataCubeProperties;
        private static readonly IDictionary<string, DimensionProperty> IdentityDimensions = GetDimensionProperties(typeof(TIdentity));
        private static readonly IDictionary<string, DimensionProperty> ElementDimensions = GetDimensionProperties(typeof(TElement));
        private static readonly IDictionary<string, DimensionProperty> ScopeDimensions = GetDimensionProperties(typeof(TScope));
        private readonly IScopeRegistry scopeRegistry;

        // ReSharper disable once StaticMemberInGenericType
        protected static readonly AspectPredicate[] AspectPredicates = { x => x.DeclaringType.IsDataCubeInterface() || x.DeclaringType.IsEnumerableInterface() };
        public override IEnumerable<AspectPredicate> Predicates => AspectPredicates;

        public DataCubeScopeInterceptor(IScopeRegistry scopeRegistry)
        {
            this.scopeRegistry = scopeRegistry;
        }


        public override void Intercept(IInvocation invocation)
        {
            if (invocation.Method.DeclaringType.IsEnumerableInterface())
            {
                var enumerator = Enumerator(invocation);
                invocation.ReturnValue = enumerator;
                return;
            }

            switch (invocation.Method.Name)
            {
                case nameof(IDataCube.GetSlices):
                    invocation.ReturnValue = GetSlices(invocation);
                    return;
                case nameof(IDataCube.GetDimensionDescriptors):
                    invocation.ReturnValue = GetDimensionDescriptors(invocation);
                    return;
                case nameof(IDataCube.Filter):
                    invocation.ReturnValue =
                        ((IFilterable)invocation.Proxy).Filter(((string, object)[])invocation.Arguments.First());
                    return;
            }

            // TODO: Anything missing? (2021/05/20, Roland Buergi)
            throw new NotImplementedException();
        }

        private IEnumerable<DimensionDescriptor> GetDimensionDescriptors(IInvocation invocation)
        {
            var isByRow = (bool)invocation.Arguments.First();
            var dimensions = (string[])invocation.Arguments.Skip(1).First();

            var identityDimensionDescriptors = TuplesUtils<TIdentity>.GetDimensionDescriptors(dimensions);
            var elementDimensionDescriptors = TuplesUtils<TElement>.GetDimensionDescriptors(dimensions);
            var dimensionDescriptors = identityDimensionDescriptors.Concat(elementDimensionDescriptors).ToList();
            
            if (typeof(TElement) != typeof(TScope))
            {
                var scopeDimensionDescriptors = TuplesUtils<TScope>.GetDimensionDescriptors(dimensions);
                dimensionDescriptors.AddRange(scopeDimensionDescriptors);
            }

            var err = String.Join(", ", dimensionDescriptors.GroupBy(x => x.SystemName).Where(g => g.Count() > 1).Select(x => $"'{x.Key}'"));
            if (err != String.Empty)
                throw new InvalidOperationException($"Duplicate dimensions: {err}");
            
            var orderedDimensionDescriptors = from dim in dimensions
                                              join descriptor in dimensionDescriptors
                                                  on dim equals descriptor.SystemName
                                              select descriptor;

            if (isByRow)
            {
                return new[] { new DimensionDescriptor(DataCubeScopesExtensions.PropertyDimension, typeof(PropertyInfo)) }
                    .Concat(orderedDimensionDescriptors);
            }

            return orderedDimensionDescriptors;
        }

        private IEnumerator Enumerator(IInvocation invocation)
        {
            if (DataCubeProperties.Length > 0)
                return EnumerateDataCubeProperties(invocation).GetEnumerator();
            return ((TScope)invocation.Proxy).RepeatOnce().GetEnumerator();
        }

        private static IEnumerable<TElement> EnumerateDataCubeProperties(IInvocation invocation)
        {
            foreach (var enumerable in DataCubeProperties.Select(p => (IEnumerable<TElement>)p.Reflector.GetValue(invocation.Proxy)))
            {
                if(enumerable != null)
                    foreach (var item in enumerable)
                    {
                        yield return item;
                    }
            }
        }

        protected object GetSlices(IInvocation invocation)
        {
            var dimensions = (string[])invocation.Arguments.First();

            var myIdentityDimensions = GetDimensions(dimensions, IdentityDimensions);

            DimensionTuple tuple = default;
            if (myIdentityDimensions.Any())
            {
                var identity = scopeRegistry.GetIdentity(invocation.Proxy);
                var identityTuple = new DimensionTuple(myIdentityDimensions.Select(x => (
                                                                                        x.DimensionSystemName,
                                                                                        x.Reflector.GetValue(identity)
                                                                                    )));
                tuple = tuple.Enrich(identityTuple);
            }

            if (typeof(TScope) != typeof(TElement) || DataElementProperties.Length == 0)
            {
                var myScopeDimensions = GetDimensions(dimensions, ScopeDimensions);
                tuple = tuple.Enrich(myScopeDimensions.Select(x => (x.DimensionSystemName, x.Reflector.GetValue(invocation.Proxy))));
            }

            if (typeof(TScope) == typeof(TElement) && DataCubeProperties.Length == 0)
            {
                return new DataSlice<TScope>((TScope)invocation.Proxy, tuple).RepeatOnce();
                // TODO V10: make test for DataCubeProperties.Length > 0 (2022/07/18, Ekaterina Mishina)
            }

            var dataCubeSlices = DataCubeProperties
                                 .Select(p => new
                                              {
                                                  DataCube = p.Reflector.GetValue(invocation.Proxy) as IDataCube<TElement>,
                                                  p.Property
                                              })
                                 .Where(x => x.DataCube != null)
                                 .SelectMany(c =>
                                             {
                                                 var propertyTuple = tuple.Enrich((DataCubeScopesExtensions.PropertyDimension, c.Property));
                                                 return c.DataCube.GetSlices(dimensions)
                                                         .Select(s => new DataSlice<TElement>(s.Data, s.Tuple.Enrich(propertyTuple)));
                                             });

            var elementDimensionProperties = GetDimensions(dimensions, ElementDimensions);
            var elementSlices = DataElementProperties.Select(p => new
                                                               {
                                                                   Element = p.Reflector.GetValue(invocation.Proxy) as TElement,
                                                                   p.Property
                                                               })
                                                  .Where(x => x.Element != null)
                                                  .Select(c =>
                                                          {
                                                              var propertyTuple = tuple.Enrich((DataCubeScopesExtensions.PropertyDimension, (object)c.Property).RepeatOnce().Concat(elementDimensionProperties.Select(dp => (dp.DimensionSystemName, dp.Reflector.GetValue(c.Element)))));
                                                              return new DataSlice<TElement>(c.Element, propertyTuple);
                                                          });

            return dataCubeSlices.Concat(elementSlices);
        }

        private static DimensionProperty[] GetDimensions(string[] dimensions, IDictionary<string, DimensionProperty> dimensionProperties)
        {
            var myIdentityDimensions = dimensions.Select(d => dimensionProperties.TryGetValue(d, out var dp) ? dp : default)
                                                 .Where(x => x != default).ToArray();
            return myIdentityDimensions;
        }

        private static IDictionary<string, DimensionProperty> GetDimensionProperties(Type type)
        {
            var identityDimensions = type
                                     .GetProperties()
                                     .Select(p => new { Property = p, DimensionAttribute = p.GetCustomAttribute<DimensionAttribute>() })
                                     .Where(p => p.DimensionAttribute != null)
                                     .Select(p =>
                                                 new DimensionProperty(p.DimensionAttribute.Name, p.DimensionAttribute.Type, p.Property))
                                     .ToDictionaryValidated(x => x.DimensionSystemName);
            return identityDimensions;
        }
    }
}