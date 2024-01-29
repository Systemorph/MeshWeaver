using OpenSmc.DataSetReader.Excel.Utils;

namespace OpenSmc.DataSetReader.Excel
{
    public interface IExcelReaderFactory
    {
        IExcelDataReader CreateBinaryReader(Stream fileStream);
        IExcelDataReader CreateBinaryReader(Stream fileStream, ReadOption option);
        IExcelDataReader CreateBinaryReader(Stream fileStream, bool convertOADate);
        IExcelDataReader CreateBinaryReader(Stream fileStream, bool convertOADate, ReadOption readOption);
        IExcelDataReader CreateOpenXmlReader(Stream fileStream);
    }
}
