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
    /// Answers every gate still shut as teardown begins — shutdown is the outcome after which none
    /// of them can open, so a delivery parked behind one must be NACKed rather than left to its
    /// own timeout. See the implementation for the measurement.
    /// </summary>
    void FailAllGatesOnShutdown();
    /// <summary>
    /// Cancels any in-progress message handlers (e.g. stuck initialization)
    /// to unblock the execution pipeline for shutdown processing.
    /// </summary>
    void CancelExecution();
}
