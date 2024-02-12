namespace OpenSmc.DataSetReader
{
    public class DataSetReadException : Exception
    {
        public DataSetReadException()
        {
        }

        public DataSetReadException(string message) : base(message)
        {
        }

        public DataSetReadException(string message, Exception innerException) : base(message, innerException)
        {
        }

    }
}
