namespace MeshWeaver.Messaging;

public record ExecutionRequest(Func<CancellationToken, Task> Action);
