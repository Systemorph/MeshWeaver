#nullable enable
using Microsoft.Extensions.Logging;
using MeshWeaver.Messaging;

namespace MeshWeaver.Activities;

public record LogMessage(string Message, LogLevel LogLevel)
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? CategoryName { get; init; }
    public IReadOnlyCollection<KeyValuePair<string, object>>? Scopes { get; init; } = [];
}

public record LogRequest(ActivityAddress ActivityAddress, params IReadOnlyCollection<LogMessage> LogMessages) : IRequest;

public record StartSubActivityRequest(string Category) : IRequest;
