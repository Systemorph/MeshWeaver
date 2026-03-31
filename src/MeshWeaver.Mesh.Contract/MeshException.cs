namespace MeshWeaver.Mesh
{
    /// <summary>
    /// Exception thrown for mesh-related errors.
    /// </summary>
    public class MeshException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the MeshException class.
        /// </summary>
        public MeshException() { }

        /// <summary>
        /// Initializes a new instance of the MeshException class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public MeshException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the MeshException class with a specified error message and inner exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public MeshException(string message, Exception innerException) : base(message, innerException) { }
    }
}
