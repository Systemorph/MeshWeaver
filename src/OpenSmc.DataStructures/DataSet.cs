using System.Collections;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace OpenSmc.DataStructures
{
    // TODO V10: We should convert this to immutable (12.02.2024, Roland Bürgi)
    [Serializable]
    public class DataSet : IDataSet, IXmlSerializable
    {
        public string DataSetName { get; set; }
        public IDataTableCollection Tables { get; } = new DataTableCollection();

        public DataSet(string name = null)
        {
            DataSetName = name;
        }

        // ReSharper disable once UnusedMember.Local This is used when deserializing
        private DataSet()
        {
        }

        public IEnumerator<IDataTable> GetEnumerator()
        {
            return Tables.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Merge(IDataSet dataSet)
        {
            foreach (var table in dataSet.Tables)
            {
                var myTable = Tables[table.TableName];
                IDictionary<int, int> map;
                if (myTable == null)
                {
                    myTable = new DataTable(table.TableName, table.Columns);
                    Tables.Add(table);
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

        public XmlSchema GetSchema()
        {
            return null;
        }

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

                var rowDict = new Dictionary<string, string>();

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
                foreach (var row in table.Rows)
                {
                    writer.WriteStartElement(table.TableName);
                    foreach (var column in table.Columns)
                    {
                        writer.WriteStartElement(column.ColumnName);
                        if (row[column.ColumnName] != null)
                            writer.WriteString(row[column.ColumnName].ToString());
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
            }
        }

        #endregion
    }

    [Serializable]
    public class DataTableCollection : IDataTableCollection
    {
        private readonly List<IDataTable> tables = new List<IDataTable>();
        private readonly Dictionary<string, int> tableNames = new Dictionary<string, int>();

        public IEnumerator<IDataTable> GetEnumerator()
        {
            return tables.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IDataTable this[int i] => tables[i];

        public IDataTable this[string name]
        {
            get
            {
                int i;
                if (tableNames.TryGetValue(name, out i))
                    return tables[i];
                return null;
            }
        }

        public int Count => tables.Count;

        public void Add(IDataTable table)
        {
            if (table.TableName == null)
                table.TableName = "Table " + tables.Count;
            tables.Add(table);
            tableNames.Add(table.TableName, tables.Count - 1);
            var internalImpl = table as DataTable;
            if (internalImpl != null)
                internalImpl.DataTableCollections.Add(this);
        }

        public IDataTable Add(string name)
        {
            var dataTable = new DataTable(name);
            Add(dataTable);
            return dataTable;
        }

        public void AddRange(IEnumerable<IDataTable> newTables)
        {
            foreach (var t in newTables)
                Add(t);
        }

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
                tableNames[table.TableName] = newIndex;
            }
        }

        public void Clear()
        {
            foreach (var internalImpl in tables.Cast<DataTable>())
                internalImpl.DataTableCollections.Remove(this);

            tables.Clear();
            tableNames.Clear();
        }

        public int IndexOf(string tableName)
        {
            return tableNames[tableName];
        }

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