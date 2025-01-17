namespace MeshWeaver.Kernel;

public record KernelEventEnvelope(string Envelope);
public record KernelCommandEnvelope(string Command, string LayoutAreaUrl);

public record SubmitCodeRequest(string Code, string LayoutAreaUrl);

public record SubscribeKernelEventsRequest;
public record UnsubscribeKernelEventsRequest;
