namespace MeshWeaver.Session;

public interface ISessionVariable : IFileReadStorageProvider, IFileWriteStorageProvider
{
    string SessionId { get; }
    CancellationToken CancellationToken { get; }
    new ISessionFileStorage FileStorage { get; }
    ISessionUser User { get; }
    void Cancel();
}