namespace OpenSmc.Session
{
    public interface ISessionContext
    {
        string SessionId { get; }
        CancellationToken CancellationToken { get; }
        void Cancel();
        void SetCancellationTokenSource(CancellationTokenSource cancellationTokenSource);
        void SetVariable(string name, object instance, Type type = null);
        void SetVariable<TVariable>(string name, TVariable instance) => SetVariable(name, instance, typeof(TVariable));
    }
}