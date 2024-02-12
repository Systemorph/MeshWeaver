using System.Collections.Immutable;
using System.Runtime.InteropServices;
using AngleSharp.Io;
using OpenSmc.DataSetReader.Csv;
using OpenSmc.DataSetReader.Excel;
using OpenSmc.DataSetReader.Excel.Utils;

namespace OpenSmc.Import;

public record ImportConfiguration
{
    internal ImmutableDictionary<string, ImportFormat> ImportFormats { get; init; } 
        = ImmutableDictionary<string, ImportFormat>.Empty
            .Add(ImportFormat.Default, new ImportFormat(ImportFormat.Default));

    public ImportConfiguration WithFormat(string format, Func<ImportFormat, ImportFormat> configuration)
        => this with
        {
            ImportFormats = ImportFormats.SetItem(format,
                configuration.Invoke(ImportFormats.GetValueOrDefault(format) ?? new ImportFormat(format)))
        };


    internal ImmutableDictionary<string, DataSetReader.Abstractions.DataSetReader> DataSetReaders { get; init; } =
        ImmutableDictionary<string, DataSetReader.Abstractions.DataSetReader>.Empty
            .Add("Csv", async (stream,options,_) => {
                using var reader = new StreamReader(stream);
                return await DataSetCsvSerializer.Parse(reader, options.Delimiter, options.WithHeaderRow, options.TypeToRestoreHeadersFrom);
            })
    .Add(ExcelExtensions.Excel10, new ExcelDataSetReader().ReadAsync)
    .Add(ExcelExtensions.Excel03, new ExcelDataSetReaderOld().ReadAsync);

    public ImportConfiguration WithDataSetReader(string fileType, DataSetReader.Abstractions.DataSetReader dataSetReader)
        => this with { DataSetReaders = DataSetReaders.SetItem(fileType, dataSetReader) };

    public ImportFormat GetFormat(string importRequestFormat)
        => ImportFormats.GetValueOrDefault(importRequestFormat);


    internal ImmutableDictionary<string, Func<ImportRequest, Stream>> StreamProviders { get; init; } = ImmutableDictionary<string, Func<ImportRequest, Stream>>.Empty;

    public ImportConfiguration WithStreamReader(string sourceId, Func<ImportRequest, Stream> reader)
        => this with { StreamProviders = StreamProviders.SetItem(sourceId, reader) };



}