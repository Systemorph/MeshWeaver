using MeshWeaver.DataStructures;

namespace MeshWeaver.DataSetReader;

/// <summary>
/// Implementations of the <see cref="ReadDataSet"/> read the data from a source and returns the data
/// in a <see cref="IDataSet"/> which then can be further processed in the import
/// </summary>
public delegate Task<(IDataSet DataSet, string? Format)> ReadDataSet(
    Stream stream,
    DataSetReaderOptions options,
    CancellationToken cancellationToken
);

/// <summary>
/// Options controlling how a <see cref="ReadDataSet"/> implementation parses its source.
/// </summary>
public record DataSetReaderOptions
{
    /// <summary>Field delimiter used when parsing delimited (CSV) content; defaults to a comma.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>Whether the first row of the source is treated as a header row; defaults to <c>true</c>.</summary>
    public bool IncludeHeaderRow { get; init; } = true;

    /// <summary>Optional CLR type describing the entity the rows map to, used to build columns when no header is present.</summary>
    public Type? EntityType { get; init; }

    /// <summary>Optional MIME content type identifying the source format.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Defines delimiter for csv and strings of csv format
    /// </summary>
    public DataSetReaderOptions WithDelimiter(char delimiter)
    {
        return this with { Delimiter = delimiter };
    }

    /// <summary>
    /// Defines whether first table of csv contains header row, by default true
    /// </summary>
    public DataSetReaderOptions WithHeaderRow(bool withHeaderRow = true)
    {
        return this with { IncludeHeaderRow = withHeaderRow };
    }

    /// <summary>
    /// Returns a copy of these options with the specified content type.
    /// </summary>
    /// <param name="contentType">The MIME content type identifying the source format.</param>
    /// <returns>A new <see cref="DataSetReaderOptions"/> with the content type applied.</returns>
    public DataSetReaderOptions WithContentType(string contentType)
    {
        return this with { ContentType = contentType };
    }

    /// <summary>
    /// Returns a copy of these options with the specified entity type.
    /// </summary>
    /// <param name="entityType">The CLR type describing the entity the rows map to.</param>
    /// <returns>A new <see cref="DataSetReaderOptions"/> with the entity type applied.</returns>
    public DataSetReaderOptions WithEntityType(Type entityType)
    {
        return this with { EntityType = entityType };
    }
}
