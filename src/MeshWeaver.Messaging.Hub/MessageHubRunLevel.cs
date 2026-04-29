namespace MeshWeaver.Messaging;

public enum MessageHubRunLevel
{
    Starting,
    Started,
    Quiescing,
    DisposeHostedHubs,
    HostedHubsDisposed,
    ShutDown,
    Dead
}
