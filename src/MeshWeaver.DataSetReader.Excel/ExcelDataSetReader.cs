using MeshWeaver.DataSetReader.Excel.Utils;
using MeshWeaver.DataStructures;

namespace MeshWeaver.DataSetReader.Excel
{
    /// <summary>
    /// Reads OpenXML (.xlsx) workbooks into an <see cref="IDataSet"/>.
    /// </summary>
    public class ExcelDataSetReader : ExcelDataSetReaderBase
    {
        private readonly IExcelReaderFactory excelDataReaderFactory;

        /// <summary>
        /// Initializes a new instance of the <c>ExcelDataSetReader</c> class.
        /// </summary>
        public ExcelDataSetReader()
        {
            excelDataReaderFactory = new ExcelReaderFactory();
        }

        /// <summary>
        /// Reads the workbook from the stream into a data set.
        /// </summary>
        /// <param name="stream">The stream containing the .xlsx workbook.</param>
        /// <returns>A tuple containing the parsed data set and the optional format descriptor.</returns>
        public (IDataSet DataSet, string? Format) Read(Stream stream)
        {
            return ReadDataSetFromFile(stream);
        }

        /// <summary>
        /// Creates an OpenXML reader over the supplied stream.
        /// </summary>
        /// <param name="stream">The stream containing the .xlsx workbook.</param>
        /// <returns>An <see cref="IExcelDataReader"/> for the workbook.</returns>
        protected override IExcelDataReader GetExcelDataReader(Stream stream)
        {
            return excelDataReaderFactory.CreateOpenXmlReader(stream);
        }
    }
}
