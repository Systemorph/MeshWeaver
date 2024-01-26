using Microsoft.Extensions.Logging;

namespace OpenSmc.Activities;

public record LogMessage(string Message, LogLevel LogLevel, DateTime Timestamp, string CategoryName, IDictionary<string, object> Scopes);