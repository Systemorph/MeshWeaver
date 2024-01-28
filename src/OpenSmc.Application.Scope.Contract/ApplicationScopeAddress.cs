using OpenSmc.Messaging;

namespace OpenSmc.Application.Scope;

public record ApplicationScopeAddress(object Host) : IHostedAddress;