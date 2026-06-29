#nullable enable
namespace MeshWeaver.DataStructures
{
    /// <summary>
    /// Factory and converter helpers for creating <c>IDataSet</c> instances and bridging
    /// to and from ADO.NET <c>System.Data.DataSet</c>.
    /// </summary>
    public static class DataSetFactory
    {
        /// <summary>Creates a new, empty data set with the given name.</summary>
        /// <param name="name">Name for the new data set.</param>
        /// <returns>The newly created data set.</returns>
        public static IDataSet Create(string name)
        {
            return new DataSet(name);
        }

        /// <summary>
        /// Converts an ADO.NET <c>System.Data.DataSet</c> into a lightweight <c>IDataSet</c>,
        /// copying its tables, columns, and rows. <c>DBNull</c> cell values are mapped to <c>null</c>.
        /// </summary>
        /// <param name="dataSet">The ADO.NET data set to convert.</param>
        /// <returns>An equivalent lightweight data set.</returns>
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

        /// <summary>
        /// Converts a lightweight <c>IDataSet</c> into an ADO.NET <c>System.Data.DataSet</c>,
        /// copying its tables, columns, and rows.
        /// </summary>
        /// <param name="dataSet">The lightweight data set to convert.</param>
        /// <returns>An equivalent ADO.NET data set.</returns>
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