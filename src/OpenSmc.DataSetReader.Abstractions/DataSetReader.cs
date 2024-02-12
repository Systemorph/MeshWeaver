using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader.Abstractions;


/// <summary>
/// Implementations of the <see cref="DataSetReader"/> read the data from a source and returns the data
/// in a <see cref="IDataSet"/> which then can be further processed in the import
/// </summary>
public delegate Task<(IDataSet DataSet, string Format)> DataSetReader (Stream stream, DataSetReaderOptions options, CancellationToken cancellationToken);

public static class MimeTypes
{
    public const string Csv = "text/csv";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Xls = "application/vnd.ms-excel";

    public static string MapFileExtension(string fileName)
    {
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

