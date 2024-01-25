using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using OpenSmc.Collections;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes
{
    public record ApplicabilityBuilder
    {
        private ImmutableList<ApplicabilityPart> Parts { get; init; } = ImmutableList<ApplicabilityPart>.Empty;

        public ApplicabilityBuilder ForScope<TScope>([NotNull] Func<ApplicabilityScopeBuilder<TScope>, ApplicabilityScopeBuilder<TScope>> builder)
        {
            var applicabilityBuilder = builder(new ApplicabilityScopeBuilder<TScope>());
            return this with
                   {
                       Parts = Parts.AddRange(applicabilityBuilder.ApplicabilityParts)
                   };
        }

        internal static ApplicabilityBuilder GetApplicabilityBuilder(Type tScope, bool isNested = false)
        {
            var methods = tScope.RepeatOnce()
                                .Concat(isNested ? Enumerable.Empty<Type>() : tScope.GetInterfaces())
                                .SelectMany(i => i.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                                  .Where(m => typeof(ApplicabilityBuilder).IsAssignableFrom(m.ReturnType) &&
                                                              m.GetParameters().Length == 1 &&
                                                              typeof(ApplicabilityBuilder).IsAssignableFrom(m.GetParameters()[0].ParameterType)))
                                .ToArray();
            if (methods.Length == 0)
                return null;

            return methods.Aggregate(new ApplicabilityBuilder(),
                                            (x, m) => (ApplicabilityBuilder)m.Invoke(null, new object[] { x }));
        }

        internal ApplicabilityMap Build()
        {
            if (!Parts.Any())
                return null;
            return new ApplicabilityMap(Parts);
        }

        public record ApplicabilityScopeBuilder<TScope>
        {
            public ApplicabilityScopeBuilder<TScope> WithApplicability<TApplicabilityScope>(Func<TScope, bool> applicabilitySelector, Func<ApplicabilityPartBuilder<TScope>, ApplicabilityPartBuilder<TScope>> options = null)
            {
                //todo: go deeper
                var nested = GetApplicabilityBuilder(typeof(TApplicabilityScope), true);
                var b = new ApplicabilityPartBuilder<TScope>(typeof(TApplicabilityScope), WrapWithCast(applicabilitySelector));
                
                var nestedParts = nested?.Parts.Select(applicabilityPart =>
                                                       {
                                                           var type = applicabilityPart.InterfaceType;
                                                           var scopeSelector = applicabilityPart.ScopeSelector;
                                                           return new ApplicabilityPartBuilder<TScope>(type, WrapWithCast(Combine(applicabilitySelector, scopeSelector)));
                                                       })
                                        .ToList() ?? Enumerable.Empty<ApplicabilityPartBuilder<TScope>>();

                var applicabilityPartBuilders = nestedParts.Concat(b.RepeatOnce());

                if (options != null)
                    applicabilityPartBuilders = applicabilityPartBuilders.Select(options.Invoke);

                return this with { ApplicabilityParts = ApplicabilityParts.Concat(applicabilityPartBuilders.Select(apb => apb.Build())).ToArray() };
            }

            private Func<TScope, bool> Combine(Func<TScope, bool> applicabilitySelector, Func<object, bool> scopeSelector)
            {
                return s => applicabilitySelector(s) && scopeSelector(s);
            }

            private Func<object, bool> WrapWithCast(Func<TScope, bool> scopeSelector)
            {
                return o => scopeSelector((TScope)o);
            }

            internal IReadOnlyCollection<ApplicabilityPart> ApplicabilityParts { get; init; } = Array.Empty<ApplicabilityPart>();
        }
    }
}