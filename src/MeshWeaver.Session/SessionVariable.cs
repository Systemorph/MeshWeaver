namespace MeshWeaver.Session;

public class SessionVariable: ISessionVariable
{
    private readonly ISessionContext sessionContext;

    public SessionVariable(ISessionFileStorage fileStorage, ISessionContext sessionContext, ISessionUser sessionUser)
    {
        this.sessionContext = sessionContext;
        FileStorage = fileStorage;
        User = sessionUser;
    }
    public string SessionId => sessionContext.SessionId;

    public void Cancel()
    {
        sessionContext.Cancel();
    }

    public CancellationToken CancellationToken => sessionContext.CancellationToken;


    public ISessionUser User { get; }
    public ISessionFileStorage FileStorage { get; }
    IFileWriteStorage IFileWriteStorageProvider.FileStorage => FileStorage;
    IFileReadStorage IFileReadStorageProvider.FileStorage => FileStorage;
}