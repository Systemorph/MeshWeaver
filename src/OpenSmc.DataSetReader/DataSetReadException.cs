using System.Runtime.Serialization;

namespace OpenSmc.DataSetReader
{
    public class DataSetReadException : /*RuntimeException*/ Exception
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

        public DataSetReadException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
