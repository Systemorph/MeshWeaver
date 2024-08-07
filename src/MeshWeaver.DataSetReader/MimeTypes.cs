namespace MeshWeaver.DataSetReader;

public static class MimeTypes
{
    public const string csv = "text/csv";
    public const string xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string xls = "application/vnd.ms-excel";

    public static string MapFileExtension(string fileName)
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
