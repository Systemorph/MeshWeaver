namespace MeshWeaver.Kernel;

public record KernelEventEnvelope(string Envelope);
public record KernelCommandEnvelope(string Command);

public record SubmitCodeRequest(string Code);

public record SubscribeKernelEventsRequest;
public record UnsubscribeKernelEventsRequest;
