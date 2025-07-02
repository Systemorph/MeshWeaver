#nullable enable
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities;

public record LogMessage(string Message, LogLevel LogLevel)
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? CategoryName { get; init; }
    public IReadOnlyCollection<KeyValuePair<string, object>>? Scopes { get; init; }
}
