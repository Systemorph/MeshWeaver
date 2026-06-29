#nullable enable
namespace MeshWeaver.DataStructures
{
    /// <summary>
    /// This factory is used to instantiate Systemorph IDataSets and also convert them to/from Ado.NET DataSets
    /// </summary>
    public interface IDataSetFactory
    {
        /// <summary>Creates a new, empty data set.</summary>
        /// <param name="name">Optional name for the data set; <c>null</c> for an unnamed set.</param>
        /// <returns>The newly created data set.</returns>
        IDataSet Create(string? name = null);
        /// <summary>Converts an ADO.NET <c>System.Data.DataSet</c> into the lightweight representation.</summary>
        /// <param name="dataSet">The ADO.NET data set to convert.</param>
        /// <returns>An equivalent lightweight data set.</returns>
        IDataSet ConvertFromAdoNet(System.Data.DataSet dataSet);
        /// <summary>Converts a lightweight data set into an ADO.NET <c>System.Data.DataSet</c>.</summary>
        /// <param name="dataSet">The lightweight data set to convert.</param>
        /// <returns>An equivalent ADO.NET data set.</returns>
        System.Data.DataSet ConvertToAdoNet(IDataSet dataSet);
    }

    /// <summary>
    /// The <see cref="IDataSet"/> is a lightweight dataset similar to the Ado.Net DataSet but with much less overhead, much faster
    /// and much less memory consuming
    /// </summary>
    public interface IDataSet : IEnumerable<IDataTable>
    {
        /// <summary>Gets or sets the name of the data set; may be <c>null</c>.</summary>
        string? DataSetName { get; set; }
        /// <summary>Gets the collection of tables contained in this data set.</summary>
        IDataTableCollection Tables { get; }
    }

    /// <summary>
    /// A collection of <c>IDataTable</c> instances belonging to an <c>IDataSet</c>,
    /// indexable by position or table name.
    /// </summary>
    public interface IDataTableCollection : IEnumerable<IDataTable>
    {
        /// <summary>Gets the table at the given position.</summary>
        /// <param name="i">Zero-based index of the table.</param>
        /// <returns>The table at that index.</returns>
        IDataTable this[int i] { get; }
        /// <summary>Gets the table with the given name, or <c>null</c> if no such table exists.</summary>
        /// <param name="name">Name of the table to look up.</param>
        /// <returns>The matching table, or <c>null</c>.</returns>
        IDataTable? this[string name] { get; }
        /// <summary>Gets the number of tables in the collection.</summary>
        int Count { get; }
        /// <summary>Adds an existing table to the collection.</summary>
        /// <param name="table">The table to add.</param>
        void Add(IDataTable table);
        /// <summary>Creates a new table with the given name and adds it to the collection.</summary>
        /// <param name="name">Name for the new table.</param>
        /// <returns>The newly created table.</returns>
        IDataTable Add(string name);
        /// <summary>Adds a sequence of existing tables to the collection.</summary>
        /// <param name="tables">The tables to add.</param>
        void AddRange(IEnumerable<IDataTable> tables);
        /// <summary>Removes the table with the given name, if present.</summary>
        /// <param name="tableName">Name of the table to remove.</param>
        void Remove(string tableName);
        /// <summary>Removes all tables from the collection.</summary>
        void Clear();
        /// <summary>Gets the position of the table with the given name.</summary>
        /// <param name="tableName">Name of the table.</param>
        /// <returns>Zero-based index of the table.</returns>
        int IndexOf(string tableName);
        /// <summary>Determines whether a table with the given name exists in the collection.</summary>
        /// <param name="tableName">Name of the table to check.</param>
        /// <returns><c>true</c> if a table with that name exists; otherwise <c>false</c>.</returns>
        bool Contains(string tableName);
    }

    /// <summary>
    /// A single table within an <c>IDataSet</c>, holding a set of columns and the rows of data under them.
    /// </summary>
    public interface IDataTable : IEnumerable<IDataRow>
    {
        /// <summary>Gets or sets the name of the table; may be <c>null</c>.</summary>
        string? TableName { get; set; }
        /// <summary>Gets the columns defined for this table.</summary>
        IDataColumnCollection Columns { get; }
        /// <summary>Gets the rows of data in this table.</summary>
        IDataRowCollection Rows { get; }
        /// <summary>Creates a new, empty row sized to this table's columns. The row is not added to the table.</summary>
        /// <returns>The newly created row.</returns>
        IDataRow NewRow();
    }

    /// <summary>
    /// A collection of <c>IDataColumn</c> definitions for an <c>IDataTable</c>,
    /// indexable by position or column name.
    /// </summary>
    public interface IDataColumnCollection : IEnumerable<IDataColumn>
    {
        /// <summary>Gets the column at the given position, or <c>null</c> if the index is out of range.</summary>
        /// <param name="i">Zero-based index of the column.</param>
        /// <returns>The column at that index, or <c>null</c>.</returns>
        IDataColumn? this[int i] { get; }
        /// <summary>Gets the column with the given name, or <c>null</c> if no such column exists.</summary>
        /// <param name="name">Name of the column to look up.</param>
        /// <returns>The matching column, or <c>null</c>.</returns>
        IDataColumn? this[string name] { get; }
        /// <summary>Gets the number of columns in the collection.</summary>
        int Count { get; }
        /// <summary>Creates a new column with the given name and data type and adds it to the collection.</summary>
        /// <param name="name">Name for the new column.</param>
        /// <param name="type">CLR type of the values stored in the column.</param>
        /// <returns>The newly created column.</returns>
        IDataColumn Add(string name, Type type);
        /// <summary>Adds a sequence of columns to the collection, copying each one's name and data type.</summary>
        /// <param name="columns">The columns to add.</param>
        void AddRange(IEnumerable<IDataColumn> columns);
        /// <summary>Removes the column with the given name, if present.</summary>
        /// <param name="columnName">Name of the column to remove.</param>
        void Remove(string columnName);
        /// <summary>Determines whether a column with the given name exists in the collection.</summary>
        /// <param name="columnName">Name of the column to check.</param>
        /// <returns><c>true</c> if a column with that name exists; otherwise <c>false</c>.</returns>
        bool Contains(string columnName);
    }

    /// <summary>Display and formatting hint for the values of a data column.</summary>
    [Serializable]
    public enum DataColumnFormat
    {
        /// <summary>No special formatting; values are shown as-is.</summary>
        Default,
        /// <summary>Values represent a percentage.</summary>
        Percentage,
        //Currency,
        //Fraction,
        //Scientific,
        //Text
    }

    /// <summary>
    /// Defines a single column of an <c>IDataTable</c>: its name, position, value type, and display format.
    /// </summary>
    public interface IDataColumn
    {
        /// <summary>Gets or sets the name of the column.</summary>
        string ColumnName { get; set; }
        /// <summary>Gets the zero-based position of the column within its table.</summary>
        int Index { get; }
        /// <summary>Gets or sets the CLR type of the values stored in this column.</summary>
        Type DataType { get; set; }
        /// <summary>Gets or sets the display/formatting hint for this column's values.</summary>
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
        object? this[int i] { get; set; }

        /// <summary>
        /// Gets or sets the value in the column of the given column name.
        /// </summary>
        /// <param name="name">Name of the column</param>
        /// <returns>Value of column</returns>
        object? this[string name] { get; set; }

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
        T Field<T>(string name, Func<object?, T>? converter);
    }

    /// <summary>The collection of <c>IDataRow</c> instances held by an <c>IDataTable</c>.</summary>
    public interface IDataRowCollection : IEnumerable<IDataRow>
    {
        /// <summary>Gets the row at the given position.</summary>
        /// <param name="i">Zero-based index of the row.</param>
        /// <returns>The row at that index.</returns>
        IDataRow this[int i] { get; }
        /// <summary>Gets the number of rows in the collection.</summary>
        int Count { get; }
        /// <summary>Adds an existing row to the collection.</summary>
        /// <param name="values">The row to add.</param>
        void Add(IDataRow values);
        /// <summary>Creates a new row from the given cell values and adds it to the collection.</summary>
        /// <param name="values">Cell values for the new row, in column order.</param>
        /// <returns>The newly created row.</returns>
        IDataRow Add(params object[] values);
        /// <summary>Removes the row at the given position.</summary>
        /// <param name="i">Zero-based index of the row to remove.</param>
        void RemoveAt(int i);
        /// <summary>Gets the position of the given row within the collection.</summary>
        /// <param name="row">The row to locate.</param>
        /// <returns>Zero-based index of the row, or -1 if not found.</returns>
        int IndexOf(IDataRow row);
    }
}