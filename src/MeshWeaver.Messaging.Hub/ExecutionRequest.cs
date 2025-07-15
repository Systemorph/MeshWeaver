using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging;

public record ExecutionRequest([property:JsonIgnore]Func<CancellationToken, Task> Action, [property: JsonIgnore] Func<Exception, Task> ExceptionCallback);
