using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace OpenSmc.Data.Persistence;

public record DataSubscription
{
    public ImmutableDictionary<string, string> Collections { get; init; } = ImmutableDictionary<string, string>.Empty;
    public JsonNode LastSynchronized { get; set; }
}