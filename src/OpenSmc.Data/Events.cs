using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record GetBatchRequest(params Type[] Types) : IRequest<WorkspaceState>;
