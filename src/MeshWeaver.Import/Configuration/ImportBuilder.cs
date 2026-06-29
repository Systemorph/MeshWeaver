using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.DataSetReader;
using MeshWeaver.DataSetReader.Csv;
using MeshWeaver.DataSetReader.Excel;
using MeshWeaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Import.Configuration;

/// <summary>
/// Mutable-by-<c>with</c> configuration accumulator for the import pipeline: registers
/// import formats, data-set readers (CSV/Excel), source stream providers and validation
/// functions. <c>ImportManager</c> folds the configured lambdas into one instance.
/// </summary>
public record ImportBuilder
{
    /// <summary>The workspace whose mapped types and data context drive import and validation.</summary>
    public IWorkspace Workspace { get; }
    /// <summary>Optional service provider used to resolve services (e.g. content access) for stream providers; may be <c>null</c>.</summary>
    public IServiceProvider? ServiceProvider { get; init; }

    /// <summary>
    /// Initializes the builder with the default readers, stream providers and the standard
    /// validation functions, plus auto-mappings for the workspace's mapped types.
    /// </summary>
    /// <param name="workspace">The workspace to import into.</param>
    /// <param name="serviceProvider">Optional service provider for resolving runtime dependencies.</param>
    public ImportBuilder(
        IWorkspace workspace,
        IServiceProvider? serviceProvider = null
    )
    {
        this.Workspace = workspace;
        this.ServiceProvider = serviceProvider;
        StreamProviders = InitializeStreamProviders();
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

    /// <summary>
    /// Registers (or appends to) a builder for the named import format. The configuration
    /// lambda is applied lazily when the format is first resolved via <see cref="GetFormat"/>.
    /// </summary>
    /// <param name="format">The format key (e.g. <see cref="ImportFormat.Default"/>).</param>
    /// <param name="configuration">Transforms the format, e.g. adding mappings or validations.</param>
    /// <returns>A new builder with the format configuration recorded.</returns>
    public ImportBuilder WithFormat(
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

    /// <summary>
    /// Resolves the configured import format, building and caching it on first request.
    /// </summary>
    /// <param name="format">The format key to resolve.</param>
    /// <returns>The built <see cref="ImportFormat"/>, or <c>null</c> if no builder was registered for the key.</returns>
    public ImportFormat? GetFormat(string format)
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

    /// <summary>
    /// Registers a data-set reader for a MIME/file type, replacing any existing reader for it.
    /// </summary>
    /// <param name="fileType">The MIME type / file type key (e.g. <c>MimeTypes.csv</c>).</param>
    /// <param name="dataSetReader">Delegate that reads a stream into an <c>IDataSet</c>.</param>
    /// <returns>A new builder with the reader registered.</returns>
    public ImportBuilder WithDataSetReader(string fileType, ReadDataSet dataSetReader) =>
        this with
        {
            DataSetReaders = DataSetReaders.SetItem(fileType, dataSetReader)
        };

    internal ImmutableDictionary<Type, Func<ImportRequest, Task<Stream>>> StreamProviders { get; init; }

    private ImmutableDictionary<Type, Func<ImportRequest, Task<Stream>>> InitializeStreamProviders() =>
        ImmutableDictionary<Type, Func<ImportRequest, Task<Stream>>>
            .Empty.Add(typeof(StringStream), CreateMemoryStream)
            .Add(typeof(EmbeddedResource), CreateEmbeddedResourceStream)
            .Add(typeof(CollectionSource), CreateCollectionStreamAsync);

    private static Task<Stream> CreateEmbeddedResourceStream(ImportRequest request)
    {
        var embeddedResource = (EmbeddedResource)request.Source;
        var assembly = embeddedResource.Assembly;
        var resourceName = $"{assembly.GetName().Name}.{embeddedResource.Resource}";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new ArgumentException($"Resource '{resourceName}' not found.");
        }
        return Task.FromResult<Stream>(stream);
    }

    private static Task<Stream> CreateMemoryStream(ImportRequest request)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(((StringStream)request.Source).Content);
        writer.Flush();
        stream.Position = 0;
        return Task.FromResult<Stream>(stream);
    }

    private async Task<Stream> CreateCollectionStreamAsync(ImportRequest request)
    {
        var collectionSource = (CollectionSource)request.Source;

        // Resolve from ContentCollection
        if (ServiceProvider == null)
            throw new ImportException("ServiceProvider is not available to resolve CollectionSource from ContentCollection");

        var contentService = ServiceProvider.GetService<IContentService>();
        if (contentService == null)
            throw new ImportException("IContentService is not registered. Ensure ContentCollections are configured.");

        var stream = await contentService.GetContentAsync(collectionSource.Collection, collectionSource.Path);
        if (stream == null)
            throw new ImportException($"Could not find content at collection '{collectionSource.Collection}' path '{collectionSource.Path}'");

        return stream;
    }

    /// <summary>
    /// Registers a provider that opens the import stream for a given <see cref="Source"/> type.
    /// </summary>
    /// <param name="sourceType">The concrete <see cref="Source"/> subtype the provider handles.</param>
    /// <param name="reader">Delegate that opens a readable stream for a request of that source type.</param>
    /// <returns>A new builder with the stream provider registered.</returns>
    public ImportBuilder WithStreamReader(
        Type sourceType,
        Func<ImportRequest, Task<Stream>> reader
    ) => this with { StreamProviders = StreamProviders.SetItem(sourceType, reader) };

    internal ImmutableList<ValidationFunction> Validations { get; init; }

    /// <summary>
    /// Adds a validation function applied to every imported instance before it is saved.
    /// </summary>
    /// <param name="validation">The validation to add.</param>
    /// <returns>A new builder with the validation appended.</returns>
    public ImportBuilder WithValidation(ValidationFunction validation) =>
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

    /// <summary>
    /// Format string (<c>{0}</c> = category type) logged when an entity references a
    /// dimension/category whose data source is not registered in the workspace.
    /// </summary>
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
                            x.Attr!.Type,
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
            //if (false)
            //{
            //    activity.LogError(
            //        string.Format(UnknownValueErrorMessage, propertyName, type.FullName, propGetter(instance))
            //    );
            //    ret = false;
            //}
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
