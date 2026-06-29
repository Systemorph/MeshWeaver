using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using MeshWeaver.DataStructures;
using MeshWeaver.Reflection;

namespace MeshWeaver.DataSetReader.Csv
{
    /// <summary>
    /// Serializes an <see cref="IDataSet"/> to CSV text and parses CSV text (including the multi-table marker syntax) back into a data set.
    /// </summary>
    public static class DataSetCsvSerializer
    {
        /// <summary>Line prefix (<c>@@</c>) marking the start of a named table within the CSV stream.</summary>
        public const string TablePrefix = "@@";

        /// <summary>Line prefix (<c>$$</c>) marking the data-set format descriptor line.</summary>
        public const string FormatPrefix = "$$";

        /// <summary>Line prefix (<c>##</c>) marking the data-set name line.</summary>
        public const string DataSetNamePrefix = "##";

        //Detects odd number of quotes located sequentially
        private static readonly Regex QuotesRegex =
            new("(?<!\")\"(\"\")*(?!\")", RegexOptions.Compiled);

        /// <summary>
        /// Serializes a data set to CSV text, emitting an <see cref="TablePrefix"/> marker line before each table.
        /// </summary>
        /// <param name="dataSet">The data set to serialize.</param>
        /// <param name="delimiter">The field delimiter to use between values.</param>
        /// <returns>The CSV representation of the data set.</returns>
        public static string Serialize(IDataSet dataSet, char delimiter)
        {
            var csvFactory = new Factory();
            var sb = new StringBuilder();
            using TextWriter textWriter = new StringWriter(sb);
            using var writer = csvFactory.CreateWriter(
                textWriter,
                new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    Delimiter = delimiter.ToString()
                }
            );
            if (!string.IsNullOrEmpty(dataSet.DataSetName))
            {
                writer.WriteField(dataSet.DataSetName);
                writer.NextRecord();
            }

            foreach (var table in dataSet.Tables)
            {
                writer.WriteField(TablePrefix + table);
                writer.NextRecord();

                foreach (var t in table.Columns)
                    writer.WriteField(t);
                writer.NextRecord();

                foreach (var row in table.Rows)
                {
                    foreach (var column in table.Columns)
                    {
                        var item = row[column.Index];
                        if (item == null || item == DBNull.Value)
                            writer.WriteField("");
                        else
                            writer.WriteField(item);
                    }
                    writer.NextRecord();
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Parses CSV text into a data set, honoring the table, format, and header-row markers.
        /// </summary>
        /// <param name="reader">The reader positioned at the start of the CSV content.</param>
        /// <param name="delimiter">The field delimiter separating values.</param>
        /// <param name="withHeaderRow">Whether the first row of each table is a header row.</param>
        /// <param name="contentType">The entity type used to build columns when the content has no leading table marker.</param>
        /// <returns>A tuple containing the parsed data set and the optional format descriptor.</returns>
        public static async Task<(IDataSet DataSet, string? Format)> Parse(
            StreamReader reader,
            char delimiter,
            bool withHeaderRow,
            Type contentType
        )
        {
            var dataSet = new DataSet();
            var content = await reader.ReadLineAsync();

            while (string.IsNullOrEmpty(content))
            {
                content = await reader.ReadLineAsync();
                if (content is null)
                    break;
            }

            string? format = null;
            if (content != null && content.StartsWith(FormatPrefix))
            {
                format = content.RemoveRedundantChars(FormatPrefix);
                content = await reader.ReadLineAsync();
            }

            /*if (content != null && content.StartsWith(DataSetNamePrefix))
            {
                dataSet.DataSetName = content.RemoveRedundantChars(DataSetNamePrefix);
                content = await reader.ReadLineAsync();
            }*/

            var csvFactory = new Factory();
            var isHeaderRow = withHeaderRow;
            IDataTable? dataTable = null;

            if (content != null && !content.StartsWith(TablePrefix))
            {
                if (contentType != null)
                {
                    dataTable = dataSet.Tables.Add(contentType.Name);
                    if (!isHeaderRow)
                        InitializeDataTableForType(contentType, dataTable);
                }
                else
                    throw new ArgumentException("Type must be specified to properly build headers");
            }

            var exit = false;
            do
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    content = await reader.ReadLineAsync();
                    exit = reader.Peek() == -1; // this will be true if we reach end of text, but if we put this to wile condition, then last line, it will not be read
                    continue;
                }

                if (content.StartsWith(TablePrefix))
                {
                    var tableName = content.RemoveRedundantChars(TablePrefix);
                    if (dataSet.Tables.Contains(tableName))
                    {
                        throw new System.Data.DuplicateNameException(
                            $"Table {tableName} was already added in source, duplication is not possible."
                        );
                    }
                    dataTable = dataSet.Tables.Add(tableName);
                    content = await reader.ReadLineAsync();
                    isHeaderRow = true;
                    continue;
                }

                content = await ParseValueInQuotes(reader, content);

                using (var lineReader = new StringReader(content))
                using (
                    var csvReader = csvFactory.CreateReader(
                        lineReader,
                        new CsvConfiguration(CultureInfo.InvariantCulture)
                        {
                            Delimiter = delimiter.ToString()
                        }
                    )
                )
                {
                    await csvReader.ReadAsync();
                    if (isHeaderRow)
                    {
                        csvReader.ReadHeader();
                        var fieldHeaders = csvReader.HeaderRecord;
                        foreach (
                            var header in fieldHeaders!.TakeWhile(header =>
                                !string.IsNullOrWhiteSpace(header)
                            )
                        )
                        {
                            dataTable?.Columns.Add(header, typeof(string));
                        }
                        isHeaderRow = false;
                    }
                    else
                    {
                        var record = csvReader.Parser.Record;
                        if (dataTable != null)
                        {
                            var row = dataTable.NewRow();
                            for (var i = 0; i < dataTable.Columns.Count; i++)
                            {
                                var sourceValue = i >= record!.Length ? null : record[i];
                                row[i] = string.IsNullOrEmpty(sourceValue) ? null : sourceValue;
                            }
                            dataTable.Rows.Add(row);
                        }
                    }
                }
                content = await reader.ReadLineAsync();
            } while (!exit);
            return (dataSet, format);
        }

        private static void InitializeDataTableForType(Type type, IDataTable dataTable)
        {
            var properties = type.GetProperties(
                    BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance
                )
                .Select(p =>
                    (Property: p, MappingOrder: p.GetSingleCustomAttribute<MappingOrderAttribute>())
                )
                .OrderBy(t => t.MappingOrder?.Order ?? int.MaxValue)
                .Select(t => new
                {
                    PropertyInfo = t.Property,
                    Length = t.MappingOrder?.Length ?? 0
                })
                .ToArray();

            foreach (var property in properties)
            {
                var propertyInfo = property.PropertyInfo!;
                var propertyType = property.PropertyInfo.PropertyType;
                if (
                    propertyType.IsGenericType
                    && propertyType.GetGenericTypeDefinition() == typeof(IList<>)
                )
                {
                    var genericArguments = propertyType.GetGenericArgumentTypes(typeof(IList<>));
                    var listElementType = genericArguments![0];
                    for (var i = 0; i < property.Length; i++)
                    {
                        dataTable.Columns.Add(string.Concat(propertyInfo.Name, i), listElementType);
                    }
                }
                else
                {
                    dataTable.Columns.Add(propertyInfo.Name, propertyType);
                }
            }
        }

        private static async Task<string> ParseValueInQuotes(TextReader reader, string content)
        {
            var singularQuotesMatches = QuotesRegex.Matches(content);
            if (singularQuotesMatches.Count % 2 == 0)
                return content;
            var sb = new StringBuilder(content);
            string? newLine;
            while ((newLine = await reader.ReadLineAsync()) != null)
            {
                // Use '\n' rather than AppendLine() so multi-line quoted fields
                // round-trip with portable LF separators on every OS — otherwise
                // Windows injects \r\n and downstream consumers see CR artifacts.
                sb.Append('\n');
                sb.Append(newLine);
                if (QuotesRegex.Matches(newLine).Count % 2 != 0)
                    break;
            }

            return sb.ToString();
        }

        private static string RemoveRedundantChars(this string content, string prefix)
        {
            return Regex.Match(content, $"[^{prefix}]*\\w", RegexOptions.Compiled).Value;
        }

        /// <summary>
        /// Reads CSV content from a stream and parses it into a data set using the supplied options.
        /// </summary>
        /// <param name="stream">The stream containing the CSV content.</param>
        /// <param name="options">Options controlling delimiter, header handling, and entity type.</param>
        /// <returns>A tuple containing the parsed data set and the optional format descriptor.</returns>
        public static async Task<(IDataSet DataSet, string? Format)> ReadAsync(
            Stream stream,
            DataSetReaderOptions options
        )
        {
            using var reader = new StreamReader(stream);
            return await Parse(
                reader,
                options.Delimiter,
                options.IncludeHeaderRow,
                options.EntityType!
            );
        }
    }
}
