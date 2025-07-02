#nullable enable
using System.Collections;
using System.Globalization;

namespace MeshWeaver.DataStructures
{
    [Serializable]
    public class DataTable : IDataTable
    {
        private string? tableName;

        public DataTable(string? name = null, IDataColumnCollection? columns = null)
        {
            tableName = name;
            Columns = new DataColumnCollection();
            if (columns != null)
                Columns.AddRange(columns.OrderBy(c => c.Index).Select(c => new DataColumn(c.ColumnName, c.DataType)));
            Rows = new DataRowCollection(this);
        }

        public string? TableName
        {
            get { return tableName; }
            set
            {
                if (tableName == value)
                    return;

                foreach (var dataTableCollection in DataTableCollections)
                    dataTableCollection.RenameTable(tableName, value);

                tableName = value;
            }
        }

        public IDataColumnCollection Columns { get; }
        public IDataRowCollection Rows { get; }
        internal List<DataTableCollection> DataTableCollections { get; } = new List<DataTableCollection>();

        public IDataRow NewRow()
        {
            var ret = new DataRow(new object[Columns.Count], this);
            return ret;
        }

        public IEnumerator<IDataRow> GetEnumerator()
        {
            return Rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public override string? ToString()
        {
            if (!string.IsNullOrEmpty(TableName))
                return TableName;
            return base.ToString();
        }
    }

    [Serializable]
    public class DataRow : IDataRow
    {
        public object[] ItemArray { get; set; }
        public IDataTable Table { get; }

        internal DataRow(object[] itemArray, IDataTable table)
        {
            ItemArray = itemArray;
            Table = table;
        }

        public IEnumerator<object> GetEnumerator()
        {
            return ItemArray.AsEnumerable().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public override string ToString()
        {
            return string.Join(",", ItemArray.Select(x => x == null ? "null" : x.ToString()));
        }

        public object? this[int i]
        {
            get
            {
                return i < ItemArray.Length ? ItemArray[i] : null;
            }
            set
            {
                ReallocateRowItems(i);
                ItemArray[i] = value;
            }
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

        public T Field<T>(string name) => Field<T>(name, null);

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

    [Serializable]
    public class DataRowCollection : IDataRowCollection
    {
        private readonly IDataTable table;
        private readonly List<IDataRow> rows = new List<IDataRow>();

        public DataRowCollection(IDataTable table)
        {
            this.table = table;
        }

        public IEnumerator<IDataRow> GetEnumerator()
        {
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IDataRow this[int i] => rows[i];

        public int Count => rows.Count;

        public void Add(IDataRow values)
        {
            rows.Add(values);
        }

        public IDataRow Add(params object[] values)
        {
            var dataRow = new DataRow(values, table);
            Add(dataRow);
            return dataRow;
        }

        public void RemoveAt(int i)
        {
            rows.RemoveAt(i);
        }

        public int IndexOf(IDataRow row)
        {
            return rows.IndexOf(row);
        }
    }

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

        public DataColumn(string columnName, Type type)
        {
            ColumnName = columnName;
            DataType = type;
        }

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

        public int Index { get; internal set; }
        public Type DataType { get; set; }
        public DataColumnFormat ColumnFormat { get; set; }

        public override string? ToString()
        {
            if (!string.IsNullOrEmpty(ColumnName))
                return ColumnName;
            return base.ToString();
        }

        internal DataColumnCollection? Columns { get; set; }
    }

    [Serializable]
    public class DataColumnCollection : IDataColumnCollection
    {
        private int nextColumnIndex;
        private readonly List<IDataColumn> columns = new List<IDataColumn>();
        private readonly IDictionary<string, int> columnNames = new Dictionary<string, int>();

        public IDataColumn? this[int i]
        {
            get
            {
                if (i >= columns.Count)
                    return null;
                return columns[i];
            }
        }

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

        public IEnumerator<IDataColumn> GetEnumerator()
        {
            return columns.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public int Count => columns.Count;

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

        public void AddRange(IEnumerable<IDataColumn> cols)
        {
            foreach (var dc in cols)
                Add(dc.ColumnName, dc.DataType);
        }

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