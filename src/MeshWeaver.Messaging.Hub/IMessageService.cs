namespace MeshWeaver.Messaging;

internal interface IMessageService : IDisposable
{
    Address Address { get; }
    IMessageDelivery RouteMessageAsync(IMessageDelivery message, CancellationToken cancellationToken);
    IMessageDelivery? Post<TMessage>(TMessage message, PostOptions opt);
    void Start();
    bool OpenGate(string name);
    bool FailGate(string name, string reason);
    /// <summary>
    /// Cancels any in-progress message handlers (e.g. stuck initialization)
    /// to unblock the execution pipeline for shutdown processing.
    /// </summary>
    void CancelExecution();
}
