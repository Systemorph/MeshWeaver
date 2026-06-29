using MeshWeaver.DataSetReader.Excel.Utils;
using MeshWeaver.DataStructures;

namespace MeshWeaver.DataSetReader.Excel
{
    /// <summary>
    /// Legacy Excel reader that first attempts the binary (.xls) format and falls back to OpenXML (.xlsx),
    /// handling files whose extension does not match their actual format.
    /// </summary>
    public class ExcelDataSetReaderOld : ExcelDataSetReaderBase
    {
        private readonly IExcelReaderFactory excelDataReaderFactory = new ExcelReaderFactory();

        /// <summary>
        /// Reads the workbook from the stream into a data set.
        /// </summary>
        /// <param name="stream">The stream containing the workbook.</param>
        /// <param name="_">Reader options (unused by this legacy reader).</param>
        /// <param name="_1">A cancellation token (unused by this legacy reader).</param>
        /// <returns>A task yielding the parsed data set and optional format descriptor.</returns>
        public Task<(IDataSet DataSet, string? Format)> ReadAsync(Stream stream, DataSetReaderOptions _, CancellationToken _1)
        {
            return Task.FromResult(ReadDataSetFromFile(stream));
        }

        /// <summary>
        /// Creates an Excel reader, trying the binary format first and falling back to OpenXML.
        /// </summary>
        /// <param name="stream">The stream containing the workbook.</param>
        /// <returns>An <see cref="IExcelDataReader"/> over the workbook.</returns>
        protected override IExcelDataReader GetExcelDataReader(Stream stream)
        {
            try
            {
                return excelDataReaderFactory.CreateBinaryReader(stream);
            }
            catch (Exception)
            {
                // since some times we have a xslx file with xsl extention we have to try following
                return excelDataReaderFactory.CreateOpenXmlReader(stream);
            }
        }
    }
}
