using Newtonsoft.Json;

namespace OpenSmc.DataStructures
{
    public class DataSetConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(DataSet);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var dataSet = new DataSet();
            var dataTableConverter = new DataTableConverter();

            CheckedRead(reader);
            if ((string)reader.Value == nameof(DataSet.DataSetName))
            {
                CheckedRead(reader);
                dataSet.DataSetName = (string)reader.Value;

                CheckedRead(reader);
            }

            while (reader.TokenType == JsonToken.PropertyName)
            {
                var tableName = (string)reader.Value;

                if (dataSet.Tables.Contains(tableName))
                    throw new InvalidOperationException($"Data contained multiple tables with name '{tableName}'");

                dataTableConverter.ReadJson(reader, typeof(DataTable), dataSet.Tables.Add(tableName), serializer);

                CheckedRead(reader);
            }
            return dataSet;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var dataSet = (DataSet)value;
            var dataTableConverter = new DataTableConverter();

            writer.WriteStartObject();

            if (!string.IsNullOrEmpty(dataSet.DataSetName))
            {
                writer.WritePropertyName(nameof(DataSet.DataSetName));
                writer.WriteValue(dataSet.DataSetName);
            }

            foreach (var dataTable in dataSet.Tables)
            {
                writer.WritePropertyName(dataTable.TableName);
                dataTableConverter.WriteJson(writer, dataTable, serializer);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        private static void CheckedRead(JsonReader reader)
        {
            if (!reader.Read())
                throw new JsonSerializationException("Unexpected end when reading DataTable.");
        }
    }

    public class DataTableConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(DataTable);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var dataTable = (DataTable)existingValue;
            if (reader.TokenType == JsonToken.PropertyName)
            {
                dataTable.TableName = (string)reader.Value;
                CheckedRead(reader);
            }
            if (reader.TokenType != JsonToken.StartArray)
                throw new JsonSerializationException($"Unexpected JSON token when reading DataTable. Expected StartArray, got {reader.TokenType}.");
            CheckedRead(reader);

            while (reader.TokenType != JsonToken.EndArray)
            {
                if (reader.TokenType != JsonToken.StartObject)
                    throw new JsonSerializationException($"Unexpected JSON token when reading DataTable. Expected StartObject, got {reader.TokenType}.");
                CheckedRead(reader);

                var rowValues = new Dictionary<string, object>();

                while (reader.TokenType == JsonToken.PropertyName)
                {
                    var columnName = (string)reader.Value ?? throw new InvalidOperationException("Column name cannot be null");
                    CheckedRead(reader);
                    var value = reader.Value;
                    CheckedRead(reader);
                    rowValues.Add(columnName, value);
                }
                foreach (var columnName in rowValues.Keys.Where(columnName => !dataTable.Columns.Contains(columnName)))
                    dataTable.Columns.Add(columnName, typeof(string));

                var row = dataTable.NewRow();
                foreach (var rowValue in rowValues)
                    row[rowValue.Key] = rowValue.Value;
                dataTable.Rows.Add(row);
                CheckedRead(reader);
            }
            return dataTable;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var table = (DataTable)value;

            writer.WriteStartArray();

            foreach (var row in table.Rows)
            {
                writer.WriteStartObject();
                foreach (var column in table.Columns)
                {
                    writer.WritePropertyName(column.ColumnName);
                    serializer.Serialize(writer, row[column.ColumnName]);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        private static void CheckedRead(JsonReader reader)
        {
            if (!reader.Read())
                throw new JsonSerializationException("Unexpected end when reading DataTable.");
        }
    }
}