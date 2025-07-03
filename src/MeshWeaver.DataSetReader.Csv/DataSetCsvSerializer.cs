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
    public static class DataSetCsvSerializer
    {
        public const string TablePrefix = "@@";
        public const string FormatPrefix = "$$";
        public const string DataSetNamePrefix = "##";

        //Detects odd number of quotes located sequentially
        private static readonly Regex QuotesRegex =
            new("(?<!\")\"(\"\")*(?!\")", RegexOptions.Compiled);

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

        public static async Task<(IDataSet DataSet, string Format)> Parse(
            StreamReader reader,
            char delimiter,
            bool withHeaderRow,
            Type contentType
        )
        {
            var dataSet = new DataSet();
            var content = await reader.ReadLineAsync();

            while (string.IsNullOrEmpty(content) && !reader.EndOfStream)
                content = await reader.ReadLineAsync();

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
                                row[i] = string.IsNullOrEmpty(sourceValue) ? null! : sourceValue;
                            }
                            dataTable.Rows.Add(row);
                        }
                    }
                }
                content = await reader.ReadLineAsync();
            } while (!exit);
            return (dataSet, format ?? "");
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
                sb.AppendLine();
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

        public static async Task<(IDataSet DataSet, string Format)> ReadAsync(
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
