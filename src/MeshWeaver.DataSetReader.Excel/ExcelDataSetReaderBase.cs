using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using MeshWeaver.DataSetReader.Excel.Utils;
using MeshWeaver.DataStructures;

namespace MeshWeaver.DataSetReader.Excel
{
    public abstract class ExcelDataSetReaderBase
    {
        public const string Format = "Format";

        protected (IDataSet DataSet, string? Format) ReadDataSetFromFile(Stream stream)
        {
            var spreadsheetDocument = SpreadsheetDocument.Open(stream, false);
            var format = spreadsheetDocument.CustomFilePropertiesPart?.Properties?.Elements<CustomDocumentProperty>()
                                                   .SingleOrDefault(x => x.Name == Format)?.InnerText;

            var dataSet = new DataSet();


            using (var dataReader = GetExcelReader(stream))
            {
                var reading = true;
                while (reading)
                {
                    var dataTable = new DataTable(dataReader.Name);
                    dataSet.Tables.Add(dataTable);

                    if (dataReader.Read())
                    {
                        for (var i = 0; i < dataReader.FieldCount; i++)
                        {
                            // ReSharper disable once ConstantNullCoalescingCondition
                            var columnName = GetUniqueColumnName(dataReader.GetString(i) ?? string.Empty, dataTable.Columns);
                            dataTable.Columns.Add(columnName, typeof(object));
                        }

                        while (dataReader.Read())
                        {
                            var row = new object[dataReader.FieldCount];
                            for (var i = 0; i < dataReader.FieldCount; i++)
                            {
                                var value = dataReader.GetValue(i);
                                var stringValue = value as string;
                                row[i] = stringValue?.Trim() ?? value;
                            }

                            dataTable.Rows.Add(row);
                        }
                    }

                    reading = dataReader.NextResult();
                }
            }

            return (dataSet, format);
        }

        private static string GetUniqueColumnName(string desiredName, IDataColumnCollection presentColumns)
        {
            var num = 1;
            while (presentColumns.Contains(desiredName))
            {
                desiredName = $"{desiredName}{num++}";
            }

            return desiredName;
        }

        private IExcelDataReader GetExcelReader(Stream stream)
        {
            var dataReader = GetExcelDataReader(stream);
            dataReader.IsFirstRowAsColumnNames = true;
            return dataReader;
        }

        protected abstract IExcelDataReader GetExcelDataReader(Stream stream);
    }
}
