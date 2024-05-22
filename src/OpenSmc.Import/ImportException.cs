namespace OpenSmc.Import;

public class ImportException : Exception
{
    public ImportException(string message, Exception innerException)
        : base(message, innerException) { }

    public ImportException(string message)
        : base(message) { }

    public ImportException() { }
}
