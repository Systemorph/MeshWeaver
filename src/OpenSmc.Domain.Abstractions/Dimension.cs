using OpenSmc.Domain.Abstractions.Attributes;

namespace OpenSmc.Domain.Abstractions
{
    public record Dimension : INamed
    {
        [IdentityProperty]
        public string SystemName { get; init; }
        public string DisplayName { get; init; }
    }
}
