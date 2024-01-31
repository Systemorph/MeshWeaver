using OpenSmc.Messaging;

namespace OpenSmc.Project.Contract;

public record NotebookElementAddress(string Id, object Host) : IHostedAddress;