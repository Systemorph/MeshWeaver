namespace MeshWeaver.Import;

/// <summary>
/// Exception raised when an import operation fails (e.g. unknown source/stream type,
/// unresolvable entity type, or an unreadable MIME type).
/// </summary>
public class ImportException : Exception
{
    /// <summary>Initializes the exception with a message and the underlying cause.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="innerException">The exception that caused this failure.</param>
    public ImportException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">A description of the failure.</param>
    public ImportException(string message)
        : base(message) { }

    /// <summary>Initializes the exception with no message.</summary>
    public ImportException() { }
}
