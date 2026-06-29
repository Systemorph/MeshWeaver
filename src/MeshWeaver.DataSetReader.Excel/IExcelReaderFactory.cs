using MeshWeaver.DataSetReader.Excel.Utils;

namespace MeshWeaver.DataSetReader.Excel
{
    /// <summary>
    /// Factory for creating <see cref="IExcelDataReader"/> instances over binary (.xls) and OpenXML (.xlsx) workbook streams.
    /// </summary>
    public interface IExcelReaderFactory
    {
        /// <summary>
        /// Creates a reader for a binary (.xls) workbook.
        /// </summary>
        /// <param name="fileStream">The stream containing the binary workbook.</param>
        /// <returns>A reader over the workbook.</returns>
        IExcelDataReader CreateBinaryReader(Stream fileStream);

        /// <summary>
        /// Creates a reader for a binary (.xls) workbook with the specified read option.
        /// </summary>
        /// <param name="fileStream">The stream containing the binary workbook.</param>
        /// <param name="option">Controls how the workbook contents are read.</param>
        /// <returns>A reader over the workbook.</returns>
        IExcelDataReader CreateBinaryReader(Stream fileStream, ReadOption option);

        /// <summary>
        /// Creates a reader for a binary (.xls) workbook, optionally converting OLE Automation dates.
        /// </summary>
        /// <param name="fileStream">The stream containing the binary workbook.</param>
        /// <param name="convertOADate">Whether to convert OLE Automation date values to <see cref="DateTime"/>.</param>
        /// <returns>A reader over the workbook.</returns>
        IExcelDataReader CreateBinaryReader(Stream fileStream, bool convertOADate);

        /// <summary>
        /// Creates a reader for a binary (.xls) workbook with date conversion and read-option control.
        /// </summary>
        /// <param name="fileStream">The stream containing the binary workbook.</param>
        /// <param name="convertOADate">Whether to convert OLE Automation date values to <see cref="DateTime"/>.</param>
        /// <param name="readOption">Controls how the workbook contents are read.</param>
        /// <returns>A reader over the workbook.</returns>
        IExcelDataReader CreateBinaryReader(Stream fileStream, bool convertOADate, ReadOption readOption);

        /// <summary>
        /// Creates a reader for an OpenXML (.xlsx) workbook.
        /// </summary>
        /// <param name="fileStream">The stream containing the OpenXML workbook.</param>
        /// <returns>A reader over the workbook.</returns>
        IExcelDataReader CreateOpenXmlReader(Stream fileStream);
    }
}
