using System.Collections.Immutable;

namespace OpenSmc.DomainDesigner.Abstractions
{
    public record DomainBuilder
    {
        public string DomainName { get; init; }

        private ImmutableList<Type> Types { get; init; } = ImmutableList<Type>.Empty;

        public DomainBuilder WithType<T>()
            where T : class
        {
            return WithType(typeof(T));
        }

        public DomainBuilder WithType(Type type)
        {
            return this with { Types = Types.Add(type) };
        }

        public DomainBuilder WithName(string domainName)
        {
            return this with { DomainName = domainName };
        }

        public DomainDescriptor ToDomain()
        {
            return new()
                   {
                       DomainName = DomainName,
                       Types = Types.ToHashSet()
                   };
        }
    }
}
