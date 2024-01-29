namespace OpenSmc.DataStructures
{
    /// <summary>
    /// This factory is used to instantiate Systemorph IDataSets and also convert them to/from Ado.NET DataSets
    /// </summary>
    public interface IDataSetFactory
    {
        IDataSet Create(string name = null);
        IDataSet ConvertFromAdoNet(System.Data.DataSet dataSet);
        System.Data.DataSet ConvertToAdoNet(IDataSet dataSet);
    }

    /// <summary>
    /// The <see cref="IDataSet"/> is a lightweight dataset similar to the Ado.Net DataSet but with much less overhead, much faster
    /// and much less memory consuming
    /// </summary>
    public interface IDataSet : IEnumerable<IDataTable>
    {
        string DataSetName { get; set; }
        IDataTableCollection Tables { get; }
    }

    public interface IDataTableCollection : IEnumerable<IDataTable>
    {
        IDataTable this[int i] { get; }
        IDataTable this[string name] { get; }
        int Count { get; }
        void Add(IDataTable table);
        IDataTable Add(string name);
        void AddRange(IEnumerable<IDataTable> tables);
        void Remove(string tableName);
        void Clear();
        int IndexOf(string tableName);
        bool Contains(string tableName);
    }

    public interface IDataTable : IEnumerable<IDataRow>
    {
        string TableName { get; set; }
        IDataColumnCollection Columns { get; }
        IDataRowCollection Rows { get; }
        IDataRow NewRow();
    }

    public interface IDataColumnCollection : IEnumerable<IDataColumn>
    {
        IDataColumn this[int i] { get; }
        IDataColumn this[string name] { get; }
        int Count { get; }
        IDataColumn Add(string name, Type type);
        void AddRange(IEnumerable<IDataColumn> columns);
        void Remove(string columnName);
        bool Contains(string columnName);
    }

    [Serializable]
    public enum DataColumnFormat
    {
        Default,
        Percentage,
        //Currency,
        //Fraction,
        //Scientific,
        //Text
    }

    public interface IDataColumn
    {
        string ColumnName { get; set; }
        int Index { get; }
        Type DataType { get; set; }
        DataColumnFormat ColumnFormat { get; set; }
    }

    /// <summary>
    /// The <see cref="IDataRow"/> represents a single row in a <see cref="IDataTable"/>.
    /// The values in the row can be access by 0-based index or by theie column name
    /// </summary>
    public interface IDataRow : IEnumerable<object>
    {
        /// <summary>
        /// Gets or sets the value in the i-th column of the row
        /// </summary>
        /// <param name="i">0-based index of the column</param>
        /// <returns>Value of column</returns>
        object this[int i] { get; set; }

        /// <summary>
        /// Gets or sets the value in the column of the given column name.
        /// </summary>
        /// <param name="name">Name of the column</param>
        /// <returns>Value of column</returns>
        object this[string name] { get; set; }

        /// <summary>
        /// Gets or sets all values of the data row as array
        /// </summary>
        object[] ItemArray { get; set; }

        /// <summary>
        /// Gets the <see cref="IDataTable"/> to which this row belongs
        /// </summary>
        IDataTable Table { get; }

        /// <summary>
        /// Gets the value in the column of the given column name and casts the value to the given type.
        /// </summary>
        /// <typeparam name="T">Data Type of the value</typeparam>
        /// <param name="name">Name of the column</param>
        /// <returns>Returns the value in the column of the given column</returns>
        T Field<T>(string name);
        /// <summary>
        /// Gets the value in the column of the given column name and casts the value to the given type.
        /// </summary>
        /// <typeparam name="T">Data Type of the value</typeparam>
        /// <param name="name">Name of the column</param>
        /// <param name="converter">Function to use to convert cell value to T, if not specified, then attempt of calling IConvertable.Convert will be performed</param>
        /// <returns>Returns the value in the column of the given column</returns>
        T Field<T>(string name, Func<object, T> converter);
    }

    public interface IDataRowCollection : IEnumerable<IDataRow>
    {
        IDataRow this[int i] { get; }
        int Count { get; }
        void Add(IDataRow values);
        IDataRow Add(params object[] values);
        void RemoveAt(int i);
        int IndexOf(IDataRow row);
    }
}