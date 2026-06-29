#nullable enable
using System.Collections;
using System.Globalization;

namespace MeshWeaver.DataStructures
{
    /// <summary>
    /// A single, mutable table: a named set of columns and the rows of data stored under them.
    /// </summary>
    [Serializable]
    public class DataTable : IDataTable
    {
        private string? tableName;

        /// <summary>Initializes a new table with the given name and an optional column template.</summary>
        /// <param name="name">Optional name for the table; <c>null</c> for an unnamed table.</param>
        /// <param name="columns">Optional columns to copy (by name and data type, in index order) into the new table.</param>
        public DataTable(string? name = null, IDataColumnCollection? columns = null)
        {
            tableName = name;
            Columns = new DataColumnCollection();
            if (columns != null)
                Columns.AddRange(columns.OrderBy(c => c.Index).Select(c => new DataColumn(c.ColumnName, c.DataType)));
            Rows = new DataRowCollection(this);
        }

        /// <summary>
        /// Gets or sets the name of the table; may be <c>null</c>. Setting it renames the table
        /// in every collection that contains it.
        /// </summary>
        public string? TableName
        {
            get { return tableName; }
            set
            {
                if (tableName == value)
                    return;

                foreach (var dataTableCollection in DataTableCollections)
                    dataTableCollection.RenameTable(tableName ?? string.Empty, value ?? string.Empty);

                tableName = value;
            }
        }

        /// <summary>Gets the columns defined for this table.</summary>
        public IDataColumnCollection Columns { get; }
        /// <summary>Gets the rows of data in this table.</summary>
        public IDataRowCollection Rows { get; }
        internal List<DataTableCollection> DataTableCollections { get; } = new List<DataTableCollection>();

        /// <summary>Creates a new, empty row sized to this table's columns. The row is not added to the table.</summary>
        /// <returns>The newly created row.</returns>
        public IDataRow NewRow()
        {
            var ret = new DataRow(new object[Columns.Count], this);
            return ret;
        }

        /// <summary>Returns an enumerator over the rows in this table.</summary>
        /// <returns>An enumerator over the table's rows.</returns>
        public IEnumerator<IDataRow> GetEnumerator()
        {
            return Rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>Returns the table name, or the default object representation if it is unnamed.</summary>
        /// <returns>The table name, or the base <c>ToString()</c> result.</returns>
        public override string? ToString()
        {
            if (!string.IsNullOrEmpty(TableName))
                return TableName;
            return base.ToString();
        }
    }

    /// <summary>A single, mutable row of an <c>IDataTable</c>, holding one cell value per column.</summary>
    [Serializable]
    public class DataRow : IDataRow
    {
        /// <summary>Gets or sets all cell values of this row as an array, in column order.</summary>
        public object[] ItemArray { get; set; }
        /// <summary>Gets the table to which this row belongs.</summary>
        public IDataTable Table { get; }

        internal DataRow(object[] itemArray, IDataTable table)
        {
            ItemArray = itemArray;
            Table = table;
        }

        /// <summary>Returns an enumerator over the cell values of this row.</summary>
        /// <returns>An enumerator over the row's cell values.</returns>
        public IEnumerator<object> GetEnumerator()
        {
            return ItemArray.AsEnumerable().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>Returns the row's cell values joined by commas, with <c>null</c> values shown as "null".</summary>
        /// <returns>A comma-separated string of the row's cell values.</returns>
        public override string ToString()
        {
            return string.Join(",", ItemArray.Select(x => x == null ? "null" : x.ToString()));
        }

        /// <summary>
        /// Gets or sets the cell value at the given column index. Reading <c>DBNull</c> yields <c>null</c>;
        /// writing past the current length grows the row up to the table's last column.
        /// </summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The cell value, or <c>null</c>.</returns>
        public object? this[int i]
        {
            get
            {
                return i < ItemArray.Length ? RemoveDbNull(ItemArray[i]) : null;
            }
            set
            {
                ReallocateRowItems(i);
                ItemArray[i] = value ?? DBNull.Value;
            }
        }

        private object? RemoveDbNull(object item)
        {
            return item is DBNull ? null : item;
        }

        private void ReallocateRowItems(int i)
        {
            if (i >= ItemArray.Length)
            {
                var lastIndex = Table.Columns.Max(c => c.Index);
                if (i > lastIndex)
                    throw new ArgumentException(string.Format("Trying to access a non-existing column. The last column is {0}", lastIndex));
                var tmp = new object[lastIndex + 1];
                ItemArray.CopyTo(tmp, 0);
                ItemArray = tmp;
            }
        }

        /// <summary>Gets or sets the cell value for the column with the given name.</summary>
        /// <param name="name">Name of the column.</param>
        /// <returns>The cell value, or <c>null</c>.</returns>
        public object? this[string name]
        {
            get { return this[GetColumnIndex(name)]; }
            set { this[GetColumnIndex(name)] = value; }
        }

        private int GetColumnIndex(string name)
        {
            var column = Table.Columns[name];
            if (column == null)
                throw new ArgumentException($"Column '{name}' does not exist in the table {{{Table}}}");
            return column.Index;
        }

        /// <summary>Gets the cell value for the named column, converted to the requested type.</summary>
        /// <typeparam name="T">Type to convert the cell value to.</typeparam>
        /// <param name="name">Name of the column.</param>
        /// <returns>The cell value converted to <typeparamref name="T"/>.</returns>
        public T Field<T>(string name) => Field<T>(name, null);

        /// <summary>
        /// Gets the cell value for the named column, converted to the requested type. When no converter
        /// is supplied, handles direct casts and string parsing for <c>DateTime</c> (OADate or invariant
        /// culture), enums, <c>Guid</c>, and other <c>IConvertible</c> types; an empty value yields the type default.
        /// </summary>
        /// <typeparam name="T">Type to convert the cell value to.</typeparam>
        /// <param name="name">Name of the column.</param>
        /// <param name="converter">Optional converter to apply to the raw cell value; when <c>null</c>, built-in conversion is used.</param>
        /// <returns>The cell value converted to <typeparamref name="T"/>.</returns>
        public T Field<T>(string name, Func<object?, T>? converter)
        {
            var value = this[name];

            if (converter != null)
                return converter(value);

            if (value is T cast)
                return cast;

            var valueStr = value?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(valueStr))
                return default(T)!;

            if (typeof(T) == typeof(DateTime))
                return (T)(object)(double.TryParse(valueStr, out var result) ? DateTime.FromOADate(result) : DateTime.Parse(valueStr, CultureInfo.InvariantCulture));

            if (typeof(T).IsEnum)
                return (T)Enum.Parse(typeof(T), valueStr);

            if (typeof(T) == typeof(Guid))
                return (T)(object)Guid.Parse(valueStr);

            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture)!;
        }
    }

    /// <summary>The default <c>IDataRowCollection</c> implementation backing a <c>DataTable</c>.</summary>
    [Serializable]
    public class DataRowCollection : IDataRowCollection
    {
        private readonly IDataTable table;
        private readonly List<IDataRow> rows = new List<IDataRow>();

        /// <summary>Initializes a new row collection bound to the given table.</summary>
        /// <param name="table">The table that owns this collection; new rows created via <c>Add</c> are bound to it.</param>
        public DataRowCollection(IDataTable table)
        {
            this.table = table;
        }

        /// <summary>Returns an enumerator over the rows in this collection.</summary>
        /// <returns>An enumerator over the collection's rows.</returns>
        public IEnumerator<IDataRow> GetEnumerator()
        {
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>Gets the row at the given position.</summary>
        /// <param name="i">Zero-based index of the row.</param>
        /// <returns>The row at that index.</returns>
        public IDataRow this[int i] => rows[i];

        /// <summary>Gets the number of rows in the collection.</summary>
        public int Count => rows.Count;

        /// <summary>Adds an existing row to the collection.</summary>
        /// <param name="values">The row to add.</param>
        public void Add(IDataRow values)
        {
            rows.Add(values);
        }

        /// <summary>Creates a new row from the given cell values, bound to the owning table, and adds it.</summary>
        /// <param name="values">Cell values for the new row, in column order.</param>
        /// <returns>The newly created row.</returns>
        public IDataRow Add(params object[] values)
        {
            var dataRow = new DataRow(values, table);
            Add(dataRow);
            return dataRow;
        }

        /// <summary>Removes the row at the given position.</summary>
        /// <param name="i">Zero-based index of the row to remove.</param>
        public void RemoveAt(int i)
        {
            rows.RemoveAt(i);
        }

        /// <summary>Gets the position of the given row within the collection.</summary>
        /// <param name="row">The row to locate.</param>
        /// <returns>Zero-based index of the row, or -1 if not found.</returns>
        public int IndexOf(IDataRow row)
        {
            return rows.IndexOf(row);
        }
    }

    /// <summary>Defines a single column of a <c>DataTable</c>: its name, position, value type, and display format.</summary>
    [Serializable]
    public class DataColumn : IDataColumn
    {
        private string columnName = string.Empty;

        internal DataColumn(string columnName, int index, Type type, DataColumnCollection columns)
            : this(columnName, type)
        {
            Index = index;
            Columns = columns;
        }

        /// <summary>Initializes a new column with the given name and value type.</summary>
        /// <param name="columnName">Name of the column.</param>
        /// <param name="type">CLR type of the values stored in the column.</param>
        public DataColumn(string columnName, Type type)
        {
            ColumnName = columnName;
            DataType = type;
        }

        /// <summary>
        /// Gets or sets the name of the column. Setting it renames the column in its owning collection.
        /// </summary>
        public string ColumnName
        {
            get => columnName;
            set
            {
                if (columnName == value)
                    return;
                Columns?.Rename(this, value);
                columnName = value;
            }
        }

        /// <summary>Gets the zero-based position of the column within its table.</summary>
        public int Index { get; internal set; }
        /// <summary>Gets or sets the CLR type of the values stored in this column.</summary>
        public Type DataType { get; set; }
        /// <summary>Gets or sets the display/formatting hint for this column's values.</summary>
        public DataColumnFormat ColumnFormat { get; set; }

        /// <summary>Returns the column name, or the default object representation if it is unnamed.</summary>
        /// <returns>The column name, or the base <c>ToString()</c> result.</returns>
        public override string? ToString()
        {
            if (!string.IsNullOrEmpty(ColumnName))
                return ColumnName;
            return base.ToString();
        }

        internal DataColumnCollection? Columns { get; set; }
    }

    /// <summary>The default <c>IDataColumnCollection</c> implementation backing a <c>DataTable</c>.</summary>
    [Serializable]
    public class DataColumnCollection : IDataColumnCollection
    {
        private int nextColumnIndex;
        private readonly List<IDataColumn> columns = new List<IDataColumn>();
        private readonly IDictionary<string, int> columnNames = new Dictionary<string, int>();

        /// <summary>Gets the column at the given position, or <c>null</c> if the index is out of range.</summary>
        /// <param name="i">Zero-based index of the column.</param>
        /// <returns>The column at that index, or <c>null</c>.</returns>
        public IDataColumn? this[int i]
        {
            get
            {
                if (i >= columns.Count)
                    return null;
                return columns[i];
            }
        }

        /// <summary>Gets the column with the given name, or <c>null</c> if no such column exists.</summary>
        /// <param name="name">Name of the column to look up.</param>
        /// <returns>The matching column, or <c>null</c>.</returns>
        public IDataColumn? this[string name]
        {
            get
            {
                int i;
                if (!columnNames.TryGetValue(name, out i))
                    return null;
                return columns[i];
            }
        }

        /// <summary>Returns an enumerator over the columns in this collection.</summary>
        /// <returns>An enumerator over the collection's columns.</returns>
        public IEnumerator<IDataColumn> GetEnumerator()
        {
            return columns.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>Gets the number of columns in the collection.</summary>
        public int Count => columns.Count;

        /// <summary>Creates a new column with the given name and data type and adds it to the collection.</summary>
        /// <param name="name">Name for the new column.</param>
        /// <param name="type">CLR type of the values stored in the column.</param>
        /// <returns>The newly created column.</returns>
        public IDataColumn Add(string name, Type type)
        {
            var column = new DataColumn(name, GetNextIndex(), type, this);
            var newIdxInColumnsArray = columns.Count;
            columns.Add(column);
            columnNames.Add(name, newIdxInColumnsArray);
            return column;
        }

        internal int GetNextIndex()
        {
            return nextColumnIndex++;
        }

        /// <summary>Adds a sequence of columns to the collection, copying each one's name and data type.</summary>
        /// <param name="cols">The columns to add.</param>
        public void AddRange(IEnumerable<IDataColumn> cols)
        {
            foreach (var dc in cols)
                Add(dc.ColumnName, dc.DataType);
        }

        /// <summary>Removes the column with the given name, if present, and reindexes the remaining columns.</summary>
        /// <param name="columnName">Name of the column to remove.</param>
        public void Remove(string columnName)
        {
            int i;
            if (columnNames.TryGetValue(columnName, out i))
            {
                columnNames.Remove(columnName);
                columns.RemoveAt(i);
                RebuildColumnIndexes(i);
            }
        }

        private void RebuildColumnIndexes(int startFrom = 0)
        {
            for (var j = startFrom; j < columns.Count; j++)
            {
                var col = columns[j];
                var newIndex = j;
                columnNames[col.ColumnName] = newIndex;
            }
        }

        /// <summary>Determines whether a column with the given name exists in the collection.</summary>
        /// <param name="columnName">Name of the column to check.</param>
        /// <returns><c>true</c> if a column with that name exists; otherwise <c>false</c>.</returns>
        public bool Contains(string columnName)
        {
            return columnNames.ContainsKey(columnName);
        }

        internal void Rename(IDataColumn column, string newName)
        {
            if (columnNames.ContainsKey(newName))
                throw new ArgumentException($"The column {newName} is already present in the list of columns, so column {column.ColumnName} could not be renamed to {newName}.");
            if (!columnNames.TryGetValue(column.ColumnName, out var originalIndex))
                throw new ArgumentException($"Column {column.ColumnName} is not present in this collection of columns.");
            columnNames.Remove(column.ColumnName);
            columnNames.Add(newName, originalIndex);
        }
    }
}
