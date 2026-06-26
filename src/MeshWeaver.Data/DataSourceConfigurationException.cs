namespace MeshWeaver.Data;

/// <summary>
/// Thrown when a data source is misconfigured.
/// </summary>
public class DataSourceConfigurationException : Exception
{
    /// <summary>
    /// Initializes the exception with an error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public DataSourceConfigurationException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes the exception with a default message.
    /// </summary>
    public DataSourceConfigurationException()
        : base() { }

    /// <summary>
    /// Initializes the exception with an error message and the exception that caused it.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public DataSourceConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
}
