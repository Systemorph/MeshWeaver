namespace MeshWeaver.Messaging;

/// <summary>
/// Thrown when a message cannot be routed to its target address — for example
/// when no route or hosted hub matches the delivery's target.
/// </summary>
public class RoutingException : Exception
{
    /// <summary>Creates a routing exception with no message.</summary>
    public RoutingException() { }

    /// <summary>Creates a routing exception with the given message.</summary>
    /// <param name="message">Description of the routing failure.</param>
    public RoutingException(string message)
        : base(message) { }

    /// <summary>Creates a routing exception with the given message and underlying cause.</summary>
    /// <param name="message">Description of the routing failure.</param>
    /// <param name="innerException">The exception that caused this routing failure.</param>
    public RoutingException(string message, Exception innerException)
        : base(message, innerException) { }
}
