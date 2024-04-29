using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.DataSetReader;
using OpenSmc.DataSetReader.Csv;
using OpenSmc.DataSetReader.Excel;
using OpenSmc.Domain;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Import;

public record ImportConfiguration
{
    public IMessageHub Hub { get; }
    public IWorkspace Workspace { get; }

    public ImportConfiguration(
        IMessageHub Hub,
        IWorkspace Workspace,
        IReadOnlyCollection<Type> mappedTypes
    )
    {
        this.Hub = Hub;
        this.Workspace = Workspace;

        Validations = ImmutableList<ValidationFunction>
            .Empty.Add(StandardValidations)
            .Add(CategoriesValidation);
        ActivityService = Hub.ServiceProvider.GetRequiredService<IActivityService>();
        if (mappedTypes.Any())
            ImportFormatBuilders = ImportFormatBuilders.Add(
                ImportFormat.Default,
                f => f.WithAutoMappingsForTypes(mappedTypes)
            );
    }

    public ImportConfiguration(IMessageHub Hub, IWorkspace Workspace)
        : this(Hub, Workspace, Array.Empty<Type>()) { }

    public IActivityService ActivityService { get; }

    private readonly ConcurrentDictionary<string, ImportFormat> ImportFormats = new();

    public ImportConfiguration WithFormat(
        string format,
        Func<ImportFormat, ImportFormat> configuration
    ) => this with { ImportFormatBuilders = ImportFormatBuilders.SetItem(format, configuration) };

    private ImmutableDictionary<
        string,
        Func<ImportFormat, ImportFormat>
    > ImportFormatBuilders { get; init; } =
        ImmutableDictionary<string, Func<ImportFormat, ImportFormat>>.Empty;

    public ImportFormat GetFormat(string format)
    {
        if (ImportFormats.TryGetValue(format, out var ret))
            return ret;

        var builder = ImportFormatBuilders.GetValueOrDefault(format);
        if (builder == null)
            return null;

        return ImportFormats.GetOrAdd(
            format,
            builder.Invoke(new ImportFormat(format, Hub, Workspace, Validations))
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

    public static string UnknownValueErrorMessage =
        "Property {0} of type {1} has unknown value {2}.";
    public static string MissingCategoryErrorMessage = "Category with name {0} was not found.";

    private static readonly ConcurrentDictionary<
        Type,
        (Type, string, Func<object, string>)[]
    > TypesWithCategoryAttributes = new();

    private bool CategoriesValidation(object instance, ValidationContext validationContext)
    {
        var type = instance.GetType();
        var dimensions = TypesWithCategoryAttributes.GetOrAdd(
            type,
            key =>
                key.GetProperties()
                    .Where(x => x.PropertyType == typeof(string))
                    .Select(x => new
                    {
                        Attr = x.GetCustomAttribute(typeof(CategoryAttribute<>)),
                        x.Name
                    })
                    .Where(x => x.Attr != null)
                    .Select(x =>
                        (
                            x.Attr.GetType().GetGenericArguments().First(),
                            x.Name,
                            CreateGetter(type, x.Name)
                        )
                    )
                    .ToArray()
        );

        var ret = true;
        foreach (var (categoryType, propertyName, propGetter) in dimensions)
        {
            if (!Workspace.MappedTypes.Contains(categoryType))
            {
                ActivityService.LogError(string.Format(MissingCategoryErrorMessage, categoryType));
                ret = false;
                continue;
            }
            var value = propGetter(instance);
            // TODO V10: Use category cache (22.03.2024, Yury Pekishev)
            if (
                !string.IsNullOrEmpty(value)
                && !(bool)
                    IsElementExistsMethod
                        .MakeGenericMethod(categoryType)
                        .InvokeAsFunction(this, Workspace, value)
            )
            {
                ActivityService.LogError(
                    string.Format(UnknownValueErrorMessage, propertyName, type.FullName, value)
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

    private readonly MethodInfo IsElementExistsMethod =
        ReflectionHelper.GetMethodGeneric<ImportConfiguration>(x =>
            x.IsElementExists<object>(null, null)
        );

    private bool IsElementExists<T>(IWorkspace workspace, string value)
        where T : class
    {
        return workspace.State.GetDataById<T>().ContainsKey(value);
    }
}
