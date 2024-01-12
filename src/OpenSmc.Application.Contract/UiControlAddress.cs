using OpenSmc.Messaging;

namespace OpenSmc.Application;

public record UiControlAddress(string Id, object Host) : IHostedAddress;
