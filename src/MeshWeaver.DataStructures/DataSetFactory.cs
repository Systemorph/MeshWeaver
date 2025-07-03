#nullable enable
namespace MeshWeaver.DataStructures
{
    public static class DataSetFactory
    {
        public static IDataSet Create(string name)
        {
            return new DataSet(name);
        }

        public static IDataSet ConvertFromAdoNet(System.Data.DataSet dataSet)
        {
            if (dataSet == null)
                throw new ArgumentNullException(nameof(dataSet));

            var ret = new DataSet(dataSet.DataSetName);
            foreach (System.Data.DataTable table in dataSet.Tables)
            {
                var t = ret.Tables.Add(table.TableName);

                foreach (System.Data.DataColumn dataColumn in table.Columns)
                    t.Columns.Add(dataColumn.ColumnName, dataColumn.DataType);

                foreach (System.Data.DataRow row in table.Rows)
                    t.Rows.Add(GetValues(row)!);
            }
            return ret;
        }

        private static object?[] GetValues(System.Data.DataRow row)
        {
            var ret = row.ItemArray.Select(x => x is DBNull ? null : x).ToArray();
            return ret;
        }

        public static System.Data.DataSet ConvertToAdoNet(IDataSet dataSet)
        {
            if (dataSet == null)
                throw new ArgumentNullException(nameof(dataSet));

            var ret = dataSet.DataSetName == null
                          ? new System.Data.DataSet()
                          : new System.Data.DataSet(dataSet.DataSetName);

            foreach (var table in dataSet.Tables)
            {
                var t = ret.Tables.Add(table.TableName);

                foreach (var dataColumn in table.Columns)
                    t.Columns.Add(dataColumn.ColumnName, dataColumn.DataType);

                foreach (var row in table.Rows)
                    t.Rows.Add(row.ItemArray);
            }

            return ret;
        }
    }
}