using MeshWeaver.Messaging;

namespace MeshWeaver.Project.Contract;

public record NotebookElementAddress(string Id, object Host) : IHostedAddress;