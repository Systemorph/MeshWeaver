using System.Collections.Immutable;
using OpenSmc.Data;
using OpenSmc.DataSetReader;
using OpenSmc.DataSetReader.Csv;
using OpenSmc.DataSetReader.Excel;

namespace OpenSmc.Import;

public record ImportConfiguration(DataContext DataContext)
{
    internal ImmutableDictionary<string, ImportFormat> ImportFormats { get; init; } 
        = ImmutableDictionary<string, ImportFormat>.Empty
            .Add(ImportFormat.Default, new ImportFormat(ImportFormat.Default).WithAutoMappings(DataContext, domain => domain));

    public ImportConfiguration WithFormat(string format, Func<ImportFormat, ImportFormat> configuration)
        => this with
        {
            ImportFormats = ImportFormats.SetItem(format,
                configuration.Invoke(ImportFormats.GetValueOrDefault(format) ?? new ImportFormat(format)))
        };



    internal ImmutableDictionary<string, ReadDataSet> DataSetReaders { get; init; } =
        ImmutableDictionary<string, ReadDataSet>.Empty.Add(MimeTypes.Csv, (stream, options, _) => DataSetCsvSerializer.ReadAsync(stream, options))
            .Add(MimeTypes.Xlsx, (stream,_,_) => Task.FromResult(new ExcelDataSetReader().Read(stream)))
            .Add(MimeTypes.Xls, new ExcelDataSetReaderOld().ReadAsync);

    public ImportConfiguration WithDataSetReader(string fileType, ReadDataSet dataSetReader)
        => this with { DataSetReaders = DataSetReaders.SetItem(fileType, dataSetReader) };

    public ImportFormat GetFormat(string importRequestFormat)
        => ImportFormats.GetValueOrDefault(importRequestFormat);


    internal ImmutableDictionary<string, Func<ImportRequest, Stream>> StreamProviders { get; init; } 
        = ImmutableDictionary<string, Func<ImportRequest, Stream>>.Empty
            .Add(nameof(String), CreateMemoryStream);

    private static Stream CreateMemoryStream(ImportRequest request)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(request.Content);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }


    public ImportConfiguration WithStreamReader(string sourceId, Func<ImportRequest, Stream> reader)
        => this with { StreamProviders = StreamProviders.SetItem(sourceId, reader) };



}