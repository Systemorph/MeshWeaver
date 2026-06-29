namespace MeshWeaver.DataSetReader.Excel.Utils
{
    /// <summary>
    /// Thrown when a BIFF (Binary Interchange File Format) record in a legacy <c>.xls</c> workbook is malformed or cannot be parsed.
    /// </summary>
    public class BiffRecordException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="BiffRecordException"/> class.</summary>
        public BiffRecordException()
        {
        }

        /// <summary>Initializes a new instance of the <see cref="BiffRecordException"/> class with an error message.</summary>
        /// <param name="message">The message describing the error.</param>
        public BiffRecordException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="BiffRecordException"/> class with an error message and inner exception.</summary>
        /// <param name="message">The message describing the error.</param>
        /// <param name="innerException">The exception that caused the current exception.</param>
        public BiffRecordException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
