namespace MeshWeaver.DataSetReader
{
    /// <summary>
    /// Exception thrown when a data set cannot be read or parsed from its source.
    /// </summary>
    public class DataSetReadException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <c>DataSetReadException</c> class.
        /// </summary>
        public DataSetReadException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <c>DataSetReadException</c> class with a descriptive message.
        /// </summary>
        /// <param name="message">The message describing the read failure.</param>
        public DataSetReadException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <c>DataSetReadException</c> class with a message and the underlying cause.
        /// </summary>
        /// <param name="message">The message describing the read failure.</param>
        /// <param name="innerException">The exception that caused the read failure.</param>
        public DataSetReadException(string message, Exception innerException) : base(message, innerException)
        {
        }

    }
}
