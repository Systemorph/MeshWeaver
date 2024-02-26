using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.DataSetReader;
using OpenSmc.DataSetReader.Csv;
using OpenSmc.DataSetReader.Excel;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

public record ImportConfiguration(IMessageHub Hub, IWorkspace Workspace)
{
    public IActivityService ActivityService { get; } = Hub.ServiceProvider.GetRequiredService<IActivityService>();

    internal ImmutableDictionary<string, ImportFormat> ImportFormats { get; init; } 

    public ImportConfiguration WithFormat(string format, Func<ImportFormat, ImportFormat> configuration)
        => this with
        {
            ImportFormatBuilders = ImportFormatBuilders.SetItem(format, configuration)
        };

    private ImmutableDictionary<string,Func<ImportFormat, ImportFormat>> ImportFormatBuilders { get; init; }
    = ImmutableDictionary<string, Func<ImportFormat, ImportFormat>>.Empty.Add(ImportFormat.Default, f => f.WithAutoMappings(domain => domain));

    public ImportConfiguration Build() =>
        this with
        {
            ImportFormats = ImportFormatBuilders
                .ToImmutableDictionary(
                    x => x.Key,
                x => x.Value.Invoke
                    (
                        new ImportFormat(x.Key, Hub, Workspace, Validations)
                            .WithValidation(StandardValidations)))
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

    internal ImmutableList<ValidationFunction> Validations { get; init; } = ImmutableList<ValidationFunction>.Empty;

    public ImportConfiguration WithValidation(ValidationFunction validation) =>
        this with { Validations = Validations.Add(validation) };

    private bool StandardValidations(object instance, ValidationContext validationContext)
    {
        var ret = true;
        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(instance, validationContext, validationResults, true);

        foreach (var validation in validationResults)
        {
            ActivityService.LogError(validation.ToString());
            ret = false;
        }
        return ret;
    }

}