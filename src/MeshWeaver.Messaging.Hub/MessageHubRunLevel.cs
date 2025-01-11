namespace MeshWeaver.Messaging;

public enum MessageHubRunLevel
{
    Starting,
    Started,
    DisposeHostedHubs,
    HostedHubsDisposed,
    ShutDown,
    Dead
}
