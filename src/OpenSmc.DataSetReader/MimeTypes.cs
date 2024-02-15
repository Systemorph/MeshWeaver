namespace OpenSmc.DataSetReader;

public static class MimeTypes
{
    public const string Csv = "text/csv";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Xls = "application/vnd.ms-excel";

    public static string MapFileExtension(string fileName)
    {
        if (fileName == null) return Csv;
        var split = fileName.Split('.');
        if (split.Length < 2)
            return Csv;

        var extension = split.Last();

        return extension switch
        {
            nameof(Csv) => Csv,
            nameof(Xlsx) => Xlsx,
            nameof(Xls) => Xls,
            _ => null
        };
    }
}

