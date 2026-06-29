namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Thrown when synchronizing a stream's state fails, for example when a change cannot be
/// applied or reconciled against the current store.
/// </summary>
public class SynchronizationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SynchronizationException"/> class.
    /// </summary>
    public SynchronizationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SynchronizationException"/> class with a message.
    /// </summary>
    /// <param name="message">The message describing the synchronization failure.</param>
    public SynchronizationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SynchronizationException"/> class with a message
    /// and the underlying cause.
    /// </summary>
    /// <param name="message">The message describing the synchronization failure.</param>
    /// <param name="innerException">The exception that caused this synchronization failure.</param>
    public SynchronizationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
