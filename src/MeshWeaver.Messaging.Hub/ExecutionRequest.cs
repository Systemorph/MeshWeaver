using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging;

public record ExecutionRequest(Func<CancellationToken, Task> Action, Func<Exception, Task> ExceptionCallback);
