using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.DataSetReader;
using MeshWeaver.DataSetReader.Csv;
using MeshWeaver.DataSetReader.Excel;
using MeshWeaver.Domain;
using MeshWeaver.Activities;

namespace MeshWeaver.Import.Configuration;

public record ImportConfiguration
{
    public IWorkspace Workspace { get; }

    public ImportConfiguration(
        IWorkspace workspace
    )
    {
        this.Workspace = workspace;
        Validations = ImmutableList<ValidationFunction>
            .Empty.Add(StandardValidations)
            .Add(CategoriesValidation);
        if (workspace.MappedTypes.Any())
            ImportFormatBuilders = ImportFormatBuilders.Add(
                ImportFormat.Default,
                [f => f.WithMappings(m => m.WithAutoMappingsForTypes(workspace.MappedTypes))]
            );
    }


    private readonly ConcurrentDictionary<string, ImportFormat> ImportFormats = new();

    public ImportConfiguration WithFormat(
        string format,
        Func<ImportFormat, ImportFormat> configuration
    ) =>
        this with
        {
            ImportFormatBuilders = ImportFormatBuilders.SetItem(
                format,
                (
                    ImportFormatBuilders.GetValueOrDefault(format)
                    ?? ImmutableList<Func<ImportFormat, ImportFormat>>.Empty
                ).Add(configuration)
            )
        };

    private ImmutableDictionary<
        string,
        ImmutableList<Func<ImportFormat, ImportFormat>>
    > ImportFormatBuilders { get; init; } =
        ImmutableDictionary<string, ImmutableList<Func<ImportFormat, ImportFormat>>>.Empty;

    public ImportFormat GetFormat(string format)
    {
        if (ImportFormats.TryGetValue(format, out var ret))
            return ret;

        var builders = ImportFormatBuilders.GetValueOrDefault(format);
        if (builders == null)
            return null;

        return ImportFormats.GetOrAdd(
            format,
            builders.Aggregate(
                new ImportFormat(format, Workspace, Validations),
                (a, b) => b.Invoke(a)
            )
        );
    }

    internal ImmutableDictionary<string, ReadDataSet> DataSetReaders { get; init; } =
        ImmutableDictionary<string, ReadDataSet>
            .Empty.Add(
                MimeTypes.csv,
                (stream, options, _) => DataSetCsvSerializer.ReadAsync(stream, options)
            )
            .Add(
                MimeTypes.xlsx,
                (stream, _, _) => Task.FromResult(new ExcelDataSetReader().Read(stream))
            )
            .Add(MimeTypes.xls, new ExcelDataSetReaderOld().ReadAsync);

    public ImportConfiguration WithDataSetReader(string fileType, ReadDataSet dataSetReader) =>
        this with
        {
            DataSetReaders = DataSetReaders.SetItem(fileType, dataSetReader)
        };

    internal ImmutableDictionary<Type, Func<ImportRequest, Stream>> StreamProviders { get; init; } =
        ImmutableDictionary<Type, Func<ImportRequest, Stream>>
            .Empty.Add(typeof(StringStream), CreateMemoryStream)
            .Add(typeof(EmbeddedResource), CreateEmbeddedResourceStream);

    private static Stream CreateEmbeddedResourceStream(ImportRequest request)
    {
        var embeddedResource = (EmbeddedResource)request.Source;
        var assembly = embeddedResource.Assembly;
        var resourceName = $"{assembly.GetName().Name}.{embeddedResource.Resource}";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new ArgumentException($"Resource '{resourceName}' not found.");
        }
        return stream;
    }

    private static Stream CreateMemoryStream(ImportRequest request)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(((StringStream)request.Source).Content);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    public ImportConfiguration WithStreamReader(
        Type sourceType,
        Func<ImportRequest, Stream> reader
    ) => this with { StreamProviders = StreamProviders.SetItem(sourceType, reader) };

    internal ImmutableList<ValidationFunction> Validations { get; init; }

    public ImportConfiguration WithValidation(ValidationFunction validation) =>
        this with
        {
            Validations = Validations.Add(validation)
        };

    private bool StandardValidations(object instance, ValidationContext validationContext, Activity activity)
    {
        var ret = true;
        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(instance, validationContext, validationResults, true);

        foreach (var validation in validationResults)
        {
            activity.LogError(validation.ToString());
            ret = false;
        }
        return ret;
    }

    public static string UnknownValueErrorMessage =
        "Property {0} of type {1} has unknown value {2}.";
    public static string MissingCategoryErrorMessage = "Category with name {0} was not found.";

    private static readonly ConcurrentDictionary<
        Type,
        (Type, string, Func<object, string>)[]
    > TypesWithCategoryAttributes = new();

    private bool CategoriesValidation(object instance, ValidationContext validationContext, Activity activity)
    {
        var type = instance.GetType();
        var dimensions = TypesWithCategoryAttributes.GetOrAdd(
            type,
            key =>
                key.GetProperties()
                    .Where(x => x.PropertyType == typeof(string))
                    .Select(x => new
                    {
                        Attr = x.GetCustomAttribute<DimensionAttribute>(),
                        x.Name
                    })
                    .Where(x => x.Attr != null)
                    .Select(x =>
                        (
                            x.Attr.Type,
                            x.Name,
                            CreateGetter(type, x.Name)
                        )
                    )
                    .ToArray()
        );

        var ret = true;
        foreach (var (dimensionType, propertyName, propGetter) in dimensions)
        {
            if (!Workspace.DataContext.DataSourcesByType.ContainsKey(dimensionType))
            {
                activity.LogError(string.Format(MissingCategoryErrorMessage, dimensionType));
                ret = false;
                continue;
            }
            //if (!string.IsNullOrEmpty(value))
            // TODO V10: Need to restore categories validation here (03.12.2024, Roland Bürgi)
            if (false)
            {
                activity.LogError(
                    string.Format(UnknownValueErrorMessage, propertyName, type.FullName, propGetter(instance))
                );
                ret = false;
            }
        }
        return ret;
    }

    private static Func<object, string> CreateGetter(Type type, string property)
    {
        var prm = Expression.Parameter(typeof(object));
        var typedPrm = Expression.Convert(prm, type);
        var propertyExpression = Expression.Property(typedPrm, property);
        return Expression.Lambda<Func<object, string>>(propertyExpression, prm).Compile();
    }


}
