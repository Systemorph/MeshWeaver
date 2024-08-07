using MeshWeaver.Messaging;

namespace MeshWeaver.Project.Contract;

public record ProjectAddress(string Id);
public record AccessAddress(object Host) : IHostedAddress;
