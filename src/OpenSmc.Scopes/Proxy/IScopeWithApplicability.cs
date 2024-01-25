using System.Linq.Expressions;
using System.Reflection;

namespace OpenSmc.Scopes.Proxy
{
    public interface IScopeWithApplicability
    {
        MethodInfo GetMethod(MethodInfo method);
    }

    public class ApplicabilityInterceptorFactory : IScopeInterceptorFactory
    {
        private readonly CreatableObjectStore<Type, IScopeWithApplicabilityInterceptor> applicabilityInterceptors = new(CreateApplicabilityInterceptor);


        private static IScopeWithApplicabilityInterceptor CreateApplicabilityInterceptor(Type type)
        {
            var applicabilityMap = GetApplicabilityMap(type);
            if (applicabilityMap == null)
                return null;
            return new ScopeWithApplicabilityInterceptor(applicabilityMap);
        }

        private static ApplicabilityMap GetApplicabilityMap(Type tScope)
        {
            return ApplicabilityBuilder.GetApplicabilityBuilder(tScope)?.Build();
        }


        public IEnumerable<IScopeInterceptor> GetInterceptors(Type tScope, IInternalScopeFactory factory)
        {
            return applicabilityInterceptors.GetInstance(tScope)?.RepeatOnce() ?? Enumerable.Empty<IScopeInterceptor>();
        }
    }

    public interface IScopeWithApplicabilityInterceptor : IScopeInterceptor, IHasAdditionalInterfaces
    {
    }

    internal class ScopeWithApplicabilityInterceptor : ScopeInterceptorBase, IScopeWithApplicabilityInterceptor
    {
        private readonly ApplicabilityMap applicabilityMap;

        internal ScopeWithApplicabilityInterceptor(ApplicabilityMap applicabilityMap)
        {
            this.applicabilityMap = applicabilityMap;
        }

        public override void Intercept(IInvocation invocation)
        {
            invocation.ReturnValue =
                applicabilityMap.GetMember(invocation.Proxy, (MethodInfo)invocation.Arguments.First());
        }

        private static readonly AspectPredicate[] SAspectPredicates =
            { x => x.DeclaringType == typeof(IScopeWithApplicability) };

        IEnumerable<Type> IHasAdditionalInterfaces.GetAdditionalInterfaces(Type scopeType) => typeof(IScopeWithApplicability).RepeatOnce().Concat(applicabilityMap.AdditionalInterfaces);
        public override IEnumerable<AspectPredicate> Predicates => SAspectPredicates;
    }

    internal class ApplicabilityMap
    {
        private readonly IReadOnlyCollection<ApplicabilityPart> applicabilityParts;
        private readonly AsyncLocal<HashSet<MethodInfo>> currentlyEvaluatingAsyncLocal = new();

        public ApplicabilityMap(IReadOnlyCollection<ApplicabilityPart> applicabilityParts)
        {
            this.applicabilityParts = applicabilityParts;
            //parsedMembers = new(MapMember);
        }

        //private readonly CreatableObjectStore<Type,  MethodInfo , MemberInfo> parsedMembers;

        internal MemberInfo GetMember(object scope, MethodInfo method)
        {
            return MapMember(scope, method);
        }

        private MemberInfo MapMember(object scope, MethodInfo method)
        {
            var currentlyEvaluating = currentlyEvaluatingAsyncLocal.Value;
            if (currentlyEvaluating == null)
                currentlyEvaluatingAsyncLocal.Value = currentlyEvaluating = new();

            if (currentlyEvaluating.Contains(method))
                throw new CyclicApplicabilityResultionException(
                                                                $"Method {method.Name} has a self-reference due to the applicability constraints.", method);
            var applicability = applicabilityParts.FirstOrDefault(p =>
                                                                  {
                                                                      try
                                                                      {
                                                                          currentlyEvaluating.Add(method);
                                                                          return p.MemberSelector(method) && p.ScopeSelector(scope);
                                                                      }
                                                                      catch (CyclicApplicabilityResultionException)
                                                                      {
                                                                          return false;
                                                                      }
                                                                      finally
                                                                      {
                                                                          currentlyEvaluating.Remove(method);
                                                                      }
                                                                  });
            if (applicability == null)
                return method;

            return GetApplicabilitySpecificMethod(method, applicability) ??
                   DefaultImplementationOfInterfacesExtensions.FindMostSpecificOverride(method,
                                                                                        applicability.InterfaceType);
        }

        private static MethodInfo GetApplicabilitySpecificMethod(MethodInfo method, ApplicabilityPart applicability)
        {
            // ReSharper disable once PossibleNullReferenceException
            var fullName = FullName(method);
            var typeInfo = applicability.InterfaceType.GetTypeInfo();
            var ret = typeInfo.DeclaredMethods
                              .FirstOrDefault(m => ImplementingName(m) == fullName);
            return ret;
        }

        private static string ImplementingName(MethodInfo methodInfo)
        {
            var split = methodInfo.Name.Split('<', '>');
            if (split.Length > 1)
                return split.First() + split.Last();
            return methodInfo.Name;
        }


        private static string FullName(MethodInfo method)
        {
            var declaringTypeFullName = method.DeclaringType.FullName;

            // C# Script creates FullName as Submission2+IScopeName ==> need to split this away.
            var split = declaringTypeFullName.Split('+');
            if (split.Length == 2 && split[0].StartsWith("Submission"))
                declaringTypeFullName = split[1];

            split = declaringTypeFullName.Split('[', ']', '`');
            if (split.Length > 1)
                declaringTypeFullName = split.First();

            return $"{declaringTypeFullName}.{method.Name}";
        }

        internal IEnumerable<Type> AdditionalInterfaces =>
            applicabilityParts.Select(p => p.InterfaceType).Distinct();
    }

    internal class CyclicApplicabilityResultionException : Exception
    {
        public MethodInfo Method { get; }

        public CyclicApplicabilityResultionException(string message, MethodInfo method)
            : base(message)
        {
            Method = method;
        }
    }


    internal record ApplicabilityPart
    {
        public Func<object, bool> ScopeSelector { get; init; }
        public Func<MethodInfo, bool> MemberSelector { get; init; } = _ => true;
        public Type InterfaceType { get; init; }
    }

    public record ApplicabilityPartBuilder<TScope>
    {
        private readonly Type interfaceType;
        private readonly Func<object, bool> scopeSelector;
        internal IReadOnlyCollection<Func<MethodInfo, bool>> MemberSelectors { get; init; } = Array.Empty<Func<MethodInfo, bool>>();

        public ApplicabilityPartBuilder(Type interfaceType, Func<object, bool> scopeSelector)
        {
            this.interfaceType = interfaceType;
            this.scopeSelector = scopeSelector;
        }

        public ApplicabilityPartBuilder<TScope> ForMember<TReturn>(Expression<Func<TScope, TReturn>> memberExpression)
        {
            MethodInfo method = memberExpression.GetMethod();
            if (method == null)
                throw new ArgumentException("Expression supplied must be ¨Property or Method selection", nameof(memberExpression));

            return this with { MemberSelectors = MemberSelectors.Concat(((Func<MemberInfo, bool>)(x => x == method)).RepeatOnce()).ToArray() };
        }

        internal ApplicabilityPart Build()
        {
            return new ApplicabilityPart
                   {
                       ScopeSelector = scopeSelector,
                       MemberSelector = MemberSelectors.Count > 0 ? MemberSelectors.Aggregate((x, y) => m => x(m) || y(m)) : _ => true,
                       InterfaceType = interfaceType
                   };
        }
    }
}