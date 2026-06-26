#nullable enable
using System.Collections;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace MeshWeaver.DataStructures
{
    // TODO V10: We should convert this to immutable (12.02.2024, Roland Bürgi)
    /// <summary>
    /// A lightweight, mutable data set: a named collection of <c>IDataTable</c> instances.
    /// Supports XML serialization via <c>IXmlSerializable</c>.
    /// </summary>
    [Serializable]
    public class DataSet : IDataSet, IXmlSerializable
    {
        /// <summary>Gets or sets the name of the data set; may be <c>null</c>.</summary>
        public string? DataSetName { get; set; }
        /// <summary>Gets the collection of tables contained in this data set.</summary>
        public IDataTableCollection Tables { get; } = new DataTableCollection();

        /// <summary>Initializes a new data set with the given name.</summary>
        /// <param name="name">Optional name for the data set; <c>null</c> for an unnamed set.</param>
        public DataSet(string? name = null)
        {
            DataSetName = name;
        }

        // ReSharper disable once UnusedMember.Local This is used when deserializing
        private DataSet()
        {
        }

        /// <summary>Returns an enumerator over the tables in this data set.</summary>
        /// <returns>An enumerator over the data set's tables.</returns>
        public IEnumerator<IDataTable> GetEnumerator()
        {
            return Tables.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Merges the tables and rows of another data set into this one. Matching tables (by name)
        /// and columns (by name) are reused; missing tables and columns are created as needed.
        /// </summary>
        /// <param name="dataSet">The data set whose tables and rows are merged in.</param>
        public void Merge(IDataSet dataSet)
        {
            foreach (var table in dataSet.Tables)
            {
                var myTable = Tables[table.TableName ?? string.Empty];
                IDictionary<int, int> map;
                if (myTable == null)
                {
                    myTable = new DataTable(table.TableName, table.Columns);
                    Tables.Add(myTable);
                    map = Enumerable.Range(0, table.Columns.Count).ToDictionary(x => x);
                }
                else
                {
                    map = (from col in table.Columns
                           join myCol in myTable.Columns on col.ColumnName equals myCol.ColumnName into joined
                           from j in joined.DefaultIfEmpty()
                           select
                           new
                           {
                               col.Index,
                               MappedTo = j != null ? j.Index : myTable.Columns.Add(col.ColumnName, col.DataType).Index
                           })
                        .ToDictionary(x => x.Index, x => x.MappedTo);
                }
                foreach (var row in table.Rows)
                {
                    var myRow = myTable.NewRow();
                    myTable.Rows.Add(myRow);
                    for (int i = 0; i < table.Columns.Count; i++)
                    {
                        myRow[map[i]] = row[i];
                    }
                }
            }
        }

        #region IXmlSerializable Implementation

        /// <summary>Returns the XML schema for this type. Always <c>null</c>, as required by <c>IXmlSerializable</c>.</summary>
        /// <returns>Always <c>null</c>.</returns>
        public XmlSchema? GetSchema()
        {
            return null;
        }

        /// <summary>Reconstructs the data set from its XML representation, recreating tables, columns, and rows.</summary>
        /// <param name="reader">Reader positioned at the serialized data set element.</param>
        public void ReadXml(XmlReader reader)
        {
            var settings = new XmlReaderSettings { IgnoreWhitespace = true };
            var subreader = XmlReader.Create(reader.ReadSubtree(), settings);

            subreader.MoveToContent();
            subreader.ReadStartElement();

            if (subreader.ReadState == ReadState.EndOfFile)
                return;

            if (subreader.Name == nameof(DataSetName))
            {
                DataSetName = subreader.ReadString();
                if (!subreader.IsEmptyElement)
                    subreader.ReadEndElement();
                else
                    subreader.Read();
            }

            while (subreader.NodeType != XmlNodeType.EndElement)
            {
                var table = Tables[subreader.Name] ?? Tables.Add(subreader.Name);
                if (subreader.IsEmptyElement)
                {
                    subreader.Read();
                    continue;
                }

                subreader.Read();

                var rowDict = new Dictionary<string, string?>();

                while (subreader.NodeType != XmlNodeType.EndElement)
                {
                    rowDict.Add(subreader.Name, subreader.IsEmptyElement ? null : subreader.ReadString());
                    if (subreader.IsEmptyElement)
                        subreader.Read();
                    else
                        subreader.ReadEndElement();
                }

                foreach (var columnName in rowDict.Keys.Where(columnName => !table.Columns.Contains(columnName)))
                    table.Columns.Add(columnName, typeof(string));

                var row = table.NewRow();
                foreach (var rowValue in rowDict)
                    row[rowValue.Key] = rowValue.Value;
                table.Rows.Add(row);

                subreader.Read();
            }
            subreader.ReadEndElement();
        }

        /// <summary>Writes the data set to XML, emitting one element per row with a child element per column.</summary>
        /// <param name="writer">Writer to which the XML is written.</param>
        public void WriteXml(XmlWriter writer)
        {
            if (!string.IsNullOrEmpty(DataSetName))
            {
                writer.WriteStartElement(nameof(DataSetName));
                writer.WriteString(DataSetName);
                writer.WriteEndElement();
            }

            foreach (var table in Tables)
            {
                if (table.TableName == null) continue;

                foreach (var row in table.Rows)
                {
                    writer.WriteStartElement(table.TableName);
                    foreach (var column in table.Columns)
                    {
                        writer.WriteStartElement(column.ColumnName);
                        if (row[column.ColumnName] != null)
                            writer.WriteString(row[column.ColumnName]!.ToString());
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
            }
        }

        #endregion
    }

    /// <summary>The default <c>IDataTableCollection</c> implementation backing a <c>DataSet</c>.</summary>
    [Serializable]
    public class DataTableCollection : IDataTableCollection
    {
        private readonly List<IDataTable> tables = new List<IDataTable>();
        private readonly Dictionary<string, int> tableNames = new Dictionary<string, int>();

        /// <summary>Returns an enumerator over the tables in this collection.</summary>
        /// <returns>An enumerator over the collection's tables.</returns>
        public IEnumerator<IDataTable> GetEnumerator()
        {
            return tables.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>Gets the table at the given position.</summary>
        /// <param name="i">Zero-based index of the table.</param>
        /// <returns>The table at that index.</returns>
        public IDataTable this[int i] => tables[i];

        /// <summary>Gets the table with the given name, or <c>null</c> if no such table exists.</summary>
        /// <param name="name">Name of the table to look up.</param>
        /// <returns>The matching table, or <c>null</c>.</returns>
        public IDataTable? this[string name]
        {
            get
            {
                int i;
                if (tableNames.TryGetValue(name, out i))
                    return tables[i];
                return null;
            }
        }

        /// <summary>Gets the number of tables in the collection.</summary>
        public int Count => tables.Count;

        /// <summary>Adds an existing table to the collection, assigning a default name if it has none.</summary>
        /// <param name="table">The table to add.</param>
        public void Add(IDataTable table)
        {
            if (table.TableName == null)
                table.TableName = "Table " + tables.Count;
            tables.Add(table);
            tableNames.Add(table.TableName, tables.Count - 1);
            var internalImpl = table as DataTable;
            internalImpl?.DataTableCollections.Add(this);
        }

        /// <summary>Creates a new table with the given name and adds it to the collection.</summary>
        /// <param name="name">Name for the new table.</param>
        /// <returns>The newly created table.</returns>
        public IDataTable Add(string name)
        {
            var dataTable = new DataTable(name);
            Add(dataTable);
            return dataTable;
        }

        /// <summary>Adds a sequence of existing tables to the collection.</summary>
        /// <param name="newTables">The tables to add.</param>
        public void AddRange(IEnumerable<IDataTable> newTables)
        {
            foreach (var t in newTables)
                Add(t);
        }

        /// <summary>Removes the table with the given name, if present.</summary>
        /// <param name="tableName">Name of the table to remove.</param>
        public void Remove(string tableName)
        {
            var table = this[tableName];
            if (table == null)
                return;

            if (!tableNames.TryGetValue(tableName, out var i))
                return;

            tables.Remove(table);
            tableNames.Remove(tableName);
            RebuildTableIndexes(i);
            var internalImpl = (DataTable)table;
            internalImpl.DataTableCollections.Remove(this);
        }

        private void RebuildTableIndexes(int startFrom = 0)
        {
            for (var j = startFrom; j < tables.Count; j++)
            {
                var table = tables[j];
                var newIndex = j;
                if (table.TableName != null)
                    tableNames[table.TableName] = newIndex;
            }
        }

        /// <summary>Removes all tables from the collection.</summary>
        public void Clear()
        {
            foreach (var internalImpl in tables.Cast<DataTable>())
                internalImpl.DataTableCollections.Remove(this);

            tables.Clear();
            tableNames.Clear();
        }

        /// <summary>Gets the position of the table with the given name.</summary>
        /// <param name="tableName">Name of the table.</param>
        /// <returns>Zero-based index of the table.</returns>
        public int IndexOf(string tableName)
        {
            return tableNames[tableName];
        }

        /// <summary>Determines whether a table with the given name exists in the collection.</summary>
        /// <param name="tableName">Name of the table to check.</param>
        /// <returns><c>true</c> if a table with that name exists; otherwise <c>false</c>.</returns>
        public bool Contains(string tableName)
        {
            return tableNames.ContainsKey(tableName);
        }

        internal void RenameTable(string oldTableName, string newTableName)
        {
            if (oldTableName == newTableName)
                return;
            int tableIndex;
            if (!tableNames.TryGetValue(oldTableName, out tableIndex))
                throw new ArgumentException(string.Format("Table with name {0} is not found in the collection.", oldTableName));

            if (tableNames.ContainsKey(newTableName))
                throw new ArgumentException(string.Format("The table {0} cannot be renamed to {1}. Collection already has another table with the same target name.", oldTableName, newTableName));

            tableNames.Remove(oldTableName);
            tableNames.Add(newTableName, tableIndex);
        }
    }
}