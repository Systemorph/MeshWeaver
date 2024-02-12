using OpenSmc.DataSetReader.Excel.Utils;
using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader.Excel
{
    public class ExcelDataSetReader : ExcelDataSetReaderBase
    {
        private readonly IExcelReaderFactory excelDataReaderFactory;

        public ExcelDataSetReader()
        {
            excelDataReaderFactory = new ExcelReaderFactory();
        }

        public (IDataSet DataSet, string Format) Read(Stream stream)
        {
            return ReadDataSetFromFile(stream);
        }

        protected override IExcelDataReader GetExcelDataReader(Stream stream)
        {
            return excelDataReaderFactory.CreateOpenXmlReader(stream);
        }
    }
}
