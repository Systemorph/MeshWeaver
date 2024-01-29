using System.Runtime.Serialization;

namespace OpenSmc.DomainDesigner.ExcelParser
{
    public class ParsingException : Exception
    {
        public ParsingException()
        {
        }

        public ParsingException(string message) : base(message)
        {
        }

        public ParsingException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public ParsingException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
