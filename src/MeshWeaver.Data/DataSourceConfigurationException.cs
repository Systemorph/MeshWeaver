namespace MeshWeaver.Data;

public class DataSourceConfigurationException : Exception
{
    public DataSourceConfigurationException(string message)
        : base(message) { }

    public DataSourceConfigurationException()
        : base() { }

    public DataSourceConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
}
