using OpenSmc.Messaging;

namespace OpenSmc.Queues;

public record QueueAddress(string Name, object Host) : IHostedAddress;


public record AcquireLockRequest : IRequest<LockAcquired>;
public record LockAcquired;

public record ReleaseLock;
