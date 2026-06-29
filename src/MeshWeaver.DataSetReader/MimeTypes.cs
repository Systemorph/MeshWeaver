namespace MeshWeaver.DataSetReader;

/// <summary>
/// Known MIME content types for the supported data-set file formats and helpers to derive them.
/// </summary>
public static class MimeTypes
{
    /// <summary>MIME type for comma-separated values (<c>text/csv</c>).</summary>
    public const string csv = "text/csv";

    /// <summary>MIME type for an OpenXML (.xlsx) spreadsheet.</summary>
    public const string xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>MIME type for a legacy binary (.xls) Excel workbook.</summary>
    public const string xls = "application/vnd.ms-excel";

    /// <summary>
    /// Maps a file name's extension to its MIME content type.
    /// </summary>
    /// <param name="fileName">The file name (or path) whose extension is inspected.</param>
    /// <returns>The matching MIME type, <see cref="csv"/> when the name has no extension, or <c>null</c> for an unrecognized extension.</returns>
    public static string? MapFileExtension(string fileName)
    {
        var split = fileName.Split('.');
        if (split.Length < 2)
            return csv;

        var extension = split.Last();

        return extension.ToLower() switch
        {
            nameof(csv) => csv,
            nameof(xlsx) => xlsx,
            nameof(xls) => xls,
            _ => null
        };
    }
}
