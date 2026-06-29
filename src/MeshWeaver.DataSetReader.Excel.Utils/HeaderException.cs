namespace MeshWeaver.DataSetReader.Excel.Utils
{
    /// <summary>
    /// Thrown when the header of a legacy <c>.xls</c> compound-document file is invalid (bad signature or byte order).
    /// </summary>
    public class HeaderException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="HeaderException"/> class.</summary>
        public HeaderException()
        {
        }

        /// <summary>Initializes a new instance of the <see cref="HeaderException"/> class with an error message.</summary>
        /// <param name="message">The message describing the error.</param>
        public HeaderException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="HeaderException"/> class with an error message and inner exception.</summary>
        /// <param name="message">The message describing the error.</param>
        /// <param name="innerException">The exception that caused the current exception.</param>
        public HeaderException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
