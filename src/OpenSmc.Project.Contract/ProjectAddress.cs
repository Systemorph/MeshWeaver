using OpenSmc.Messaging;

namespace OpenSmc.Project.Contract;

public record ProjectAddress(string Id);
public record AccessAddress(object Host) : IHostedAddress;
