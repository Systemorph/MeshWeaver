using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Data;
using MeshWeaver.DataStructures;
using MeshWeaver.Import.Implementation;
using Activity = MeshWeaver.Data.Activity;

namespace MeshWeaver.Import.Configuration;

/// <summary>
/// An immutable, named import format: an ordered pipeline of import functions (mapping
/// data-set tables into an <c>EntityStore</c>) plus the validations applied to the result.
/// </summary>
/// <param name="Format">The format key identifying this format (e.g. <see cref="Default"/>).</param>
/// <param name="Workspace">The workspace whose mapped types and hub services back the import.</param>
/// <param name="Validations">Validation functions run against every imported instance.</param>
public record ImportFormat(
    string Format,
    IWorkspace Workspace,
    ImmutableList<ValidationFunction> Validations)
{
    /// <summary>The key for the default, convention-based import format.</summary>
    public const string Default = nameof(Default);

    private ImmutableList<ImportFunctionAsync> ImportFunctions { get; init; } =
        ImmutableList<ImportFunctionAsync>.Empty;

    /// <summary>
    /// Appends an asynchronous import function to the pipeline.
    /// </summary>
    /// <param name="importFunctionAsync">The async import step to add.</param>
    /// <returns>A new format with the step appended.</returns>
    public ImportFormat WithImportFunction(ImportFunctionAsync importFunctionAsync) =>
        this with
        {
            ImportFunctions = ImportFunctions.Add(importFunctionAsync)
        };
    /// <summary>
    /// Appends a synchronous import function to the pipeline (wrapped as a completed task).
    /// </summary>
    /// <param name="importFunction">The synchronous import step to add.</param>
    /// <returns>A new format with the step appended.</returns>
    public ImportFormat WithImportFunction(ImportFunction importFunction) =>
        this with
        {
            ImportFunctions = ImportFunctions.Add((req,ds,ws,esu,ct)=>Task.FromResult(importFunction.Invoke(req, ds, ws, esu, ct)))
        };



    /// <summary>
    /// Runs every import function in order against the data set, validates the result and
    /// returns the populated store.
    /// </summary>
    /// <param name="importRequest">The originating import request.</param>
    /// <param name="dataSet">The parsed source data set.</param>
    /// <param name="activity">Activity used to log validation errors; may be <c>null</c>.</param>
    /// <param name="cancellationToken">Token observed between pipeline steps.</param>
    /// <returns>The imported <c>EntityStore</c>, or <c>null</c> if cancellation was requested.</returns>
    public async Task<EntityStore?> Import(
        ImportRequest importRequest,
        IDataSet dataSet,
        Activity? activity, 
        CancellationToken cancellationToken
    )
    {
        var store = new EntityStore();
        foreach (var importFunction in ImportFunctions)
        {
            if(cancellationToken.IsCancellationRequested)
                return null;
            store = await importFunction.Invoke(importRequest, dataSet, Workspace, store, cancellationToken);
        }


        Validate(store, activity);
        return store;
    }


    private void Validate(EntityStore importedInstances, Activity? activity)
    {
        if (activity is null)
            return;
        var hasError = false;
        var validationCache = new Dictionary<object, object?>();
        foreach (var item in importedInstances.Collections.SelectMany(x => x.Value.Instances.Values))
        {
            if (item == null)
                continue;
            foreach (var validation in Validations)
                hasError =
                    !validation(
                        item,
                        new ValidationContext(item, Workspace.Hub.ServiceProvider, validationCache),
                        activity
                    ) || hasError;
        }

        if(hasError)
            activity.LogError(ImportManager.ImportFailed);

    }

    /// <summary>
    /// An asynchronous import step: transforms the accumulating store using the data set.
    /// </summary>
    /// <param name="importRequest">The originating import request.</param>
    /// <param name="dataSet">The parsed source data set.</param>
    /// <param name="workspace">The target workspace.</param>
    /// <param name="storeAndUpdates">The store accumulated by prior steps.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The updated store.</returns>
    public delegate Task<EntityStore> ImportFunctionAsync(
        ImportRequest importRequest,
        IDataSet dataSet,
        IWorkspace workspace,
        EntityStore storeAndUpdates,
        CancellationToken cancellationToken
    );
    /// <summary>
    /// A synchronous import step: transforms the accumulating store using the data set.
    /// </summary>
    /// <param name="importRequest">The originating import request.</param>
    /// <param name="dataSet">The parsed source data set.</param>
    /// <param name="workspace">The target workspace.</param>
    /// <param name="storeAndUpdates">The store accumulated by prior steps.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The updated store.</returns>
    public delegate EntityStore ImportFunction(
        ImportRequest importRequest,
        IDataSet dataSet,
        IWorkspace workspace,
        EntityStore storeAndUpdates,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Adds an import step that maps data-set tables to entities via the configured
    /// <see cref="ImportMapContainer"/>.
    /// </summary>
    /// <param name="config">Configures the table-to-type mappings to apply.</param>
    /// <returns>A new format with the mapping step appended.</returns>
    public ImportFormat WithMappings(Func<ImportMapContainer, ImportMapContainer> config) =>
        WithImportFunction((_, dataSet, _, store, _) =>
            config(new(Workspace)).ImportAsync(dataSet, store)
        );

    /// <summary>
    /// Adds convention-based mappings for every type registered in the workspace.
    /// </summary>
    /// <returns>A new format with auto-mappings appended.</returns>
    public ImportFormat WithAutoMappings() => WithAutoMappingsForTypes(Workspace.MappedTypes);

    /// <summary>
    /// Adds a validation rule applied to imported instances; a <c>null</c> rule is ignored.
    /// </summary>
    /// <param name="validationRule">The validation to add.</param>
    /// <returns>A new format with the validation appended, or this instance if the rule was <c>null</c>.</returns>
    public ImportFormat WithValidation(ValidationFunction validationRule)
    {
        if (validationRule == null)
            return this;
        return this with { Validations = Validations.Add(validationRule) };
    }

    internal ImportFormat WithAutoMappingsForTypes(IReadOnlyCollection<Type> types) =>
        WithMappings(x => types.Aggregate(x, (a, b) => a.MapType(b)));

}

/// <summary>
/// Validates a single imported instance, logging any failures to the activity.
/// </summary>
/// <param name="instance">The imported instance to validate.</param>
/// <param name="context">Validation context carrying services and a shared cache.</param>
/// <param name="activity">Activity to which validation errors are logged.</param>
/// <returns><c>true</c> if the instance is valid; otherwise <c>false</c>.</returns>
public delegate bool ValidationFunction(object instance, ValidationContext context, Activity activity);
