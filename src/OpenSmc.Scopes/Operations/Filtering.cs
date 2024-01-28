using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Collections;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.Operations
{
    public record FilterPart
    {
        public MethodInfo Method { get; init; }
        public Func<object, object, object> Filter { get; init; }
        public Type ArgumentType { get; init; }
        public string Name { get; init; }
    }

    public record FilterBuilder<TScope>
    {
        internal IReadOnlyCollection<FilterPart> FilterParts { get; init; } = Array.Empty<FilterPart>();

        public FilterBuilder<TScope> ForMember<TMember>(Expression<Func<TScope, TMember>> member, Func<FilterPartBuilder<TScope, TMember>, FilterPart> partBuilder)
        {
            var method = member.GetMethod();
            return this with
                   {
                       FilterParts = FilterParts.Concat(partBuilder(new FilterPartBuilder<TScope, TMember>(method, method.Name))
                                                            .RepeatOnce()).ToArray()
                   };
        }
    }

    public record FilterPartBuilder<TScope, TMember>
    {
        private readonly MethodInfo method;
        private readonly string filterName;

        public FilterPartBuilder(MethodInfo method, string filterName)
        {
            this.method = method;
            this.filterName = filterName;
        }

        public FilterPartBuilder<TScope, TMember> WithName(string name)
        {
            return new(method, name);
        }

        public FilterPart Filter<TFilterArgs>(Func<TMember, TFilterArgs, TMember> filter)
        {
            return new FilterPart
                   {
                       Name = filterName,
                       ArgumentType = typeof(TFilterArgs),
                       Filter = (o, args) => filter((TMember)o, (TFilterArgs)args),
                       Method = method
                   };
        }
    }
}