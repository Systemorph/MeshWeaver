#nullable enable
using Microsoft.Extensions.Logging;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// A single log entry recorded against an <see cref="ActivityLog"/>.
/// </summary>
/// <param name="Message">The log message text.</param>
/// <param name="LogLevel">The severity of the message.</param>
public record LogMessage(string Message, LogLevel LogLevel)
{
    /// <summary>UTC timestamp when the message was created.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    /// <summary>The logging category that produced the message, if any.</summary>
    public string? CategoryName { get; init; }
    /// <summary>The active logging scopes at the time the message was created, if any.</summary>
    public IReadOnlyCollection<KeyValuePair<string, object>>? Scopes { get; init; } = [];
}

