using System.Collections.Immutable;
using OpenSmc.Data;
using OpenSmc.DataSetReader.Abstractions;
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



    internal ImmutableDictionary<string, DataSetReader.Abstractions.DataSetReader> DataSetReaders { get; init; } =
        ImmutableDictionary<string, DataSetReader.Abstractions.DataSetReader>.Empty
            .Add(MimeTypes.Csv, async (stream,options,_) => {
                using var reader = new StreamReader(stream);
                return await DataSetCsvSerializer.Parse(reader, options.Delimiter, options.WithHeaderRow, options.TypeToRestoreHeadersFrom);
            })
    .Add(MimeTypes.Xlsx, new ExcelDataSetReader().ReadAsync)
    .Add(MimeTypes.Xls, new ExcelDataSetReaderOld().ReadAsync);

    public ImportConfiguration WithDataSetReader(string fileType, DataSetReader.Abstractions.DataSetReader dataSetReader)
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