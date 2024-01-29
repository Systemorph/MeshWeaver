using OpenSmc.DataSetReader.Abstractions;
using OpenSmc.DataSetReader.Excel.Utils;
using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader.Excel
{
    public class ExcelDataSetReaderOld : ExcelDataSetReaderBase, IDataSetReader
    {
        private readonly IExcelReaderFactory excelDataReaderFactory;

        public ExcelDataSetReaderOld()
        {
            excelDataReaderFactory = new ExcelReaderFactory();
        }

        public Task<(IDataSet DataSet, string Format)> ReadAsync(Stream stream, DataSetReaderOptions options, CancellationToken cancellationToken)
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
