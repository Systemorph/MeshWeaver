using MeshWeaver.DataSetReader.Excel.Utils;
using MeshWeaver.DataStructures;

namespace MeshWeaver.DataSetReader.Excel
{
    public class ExcelDataSetReaderOld : ExcelDataSetReaderBase
    {
        private readonly IExcelReaderFactory excelDataReaderFactory = new ExcelReaderFactory();

        public Task<(IDataSet DataSet, string? Format)> ReadAsync(Stream stream, DataSetReaderOptions _, CancellationToken _1)
        {
            return Task.FromResult(ReadDataSetFromFile(stream));
        }

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
