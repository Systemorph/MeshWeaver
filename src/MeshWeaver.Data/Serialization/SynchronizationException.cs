namespace MeshWeaver.Data.Serialization;

public class SynchronizationException : Exception
{
    public SynchronizationException(string message) : base(message)
    {
    }

    public SynchronizationException(string message, Exception innerException) : base(message, innerException)
    {
    }

}
