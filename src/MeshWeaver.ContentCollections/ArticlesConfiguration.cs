using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public record ArticlesConfiguration
{
    internal ImmutableList<string> Collections { get; init; } = [];
    internal ImmutableList<Address> Addresses { get; init; } = [];

    public ArticlesConfiguration WithCollection(params IEnumerable<string> collections) =>
        this with { Collections = Collections.AddRange(collections) };
    public ArticlesConfiguration WithAddress(params IEnumerable<Address> addresses) =>
            this with { Addresses = Addresses.AddRange(addresses) };
}
