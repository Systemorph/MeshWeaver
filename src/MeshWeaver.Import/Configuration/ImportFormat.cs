using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Data;
using MeshWeaver.DataStructures;
using MeshWeaver.Import.Implementation;
using Microsoft.Extensions.Logging;
using Activity = MeshWeaver.Activities.Activity;

namespace MeshWeaver.Import.Configuration;

public record ImportFormat(
    string Format,
    IWorkspace Workspace,
    ImmutableList<ValidationFunction> Validations)
{
    public const string Default = nameof(Default);

    private ImmutableList<ImportFunctionAsync> ImportFunctions { get; init; } =
        ImmutableList<ImportFunctionAsync>.Empty;

    public ImportFormat WithImportFunction(ImportFunctionAsync importFunctionAsync) =>
        this with
        {
            ImportFunctions = ImportFunctions.Add(importFunctionAsync)
        };
    public ImportFormat WithImportFunction(ImportFunction importFunction) =>
        this with
        {
            ImportFunctions = ImportFunctions.Add((req,ds,ws,esu)=>Task.FromResult(importFunction.Invoke(req,ds,ws,esu)))
        };



    public async Task<EntityStore> Import(
        ImportRequest importRequest,
        IDataSet dataSet,
        Activity activity
    )
    {
        var store = new EntityStore();
        foreach (var importFunction in ImportFunctions)
        {
            store = await importFunction.Invoke(importRequest, dataSet, Workspace, store);
        }


        Validate(store, activity);
        if (activity.HasErrors())
            return null;
        return store;
    }


    private void Validate(EntityStore importedInstances, Activity activity)
    {
        var hasError = false;
        var validationCache = new Dictionary<object, object>();
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

    public delegate Task<EntityStore> ImportFunctionAsync(
        ImportRequest importRequest,
        IDataSet dataSet,
        IWorkspace workspace,
        EntityStore storeAndUpdates
    );
    public delegate EntityStore ImportFunction(
        ImportRequest importRequest,
        IDataSet dataSet,
        IWorkspace workspace,
        EntityStore storeAndUpdates
    );

    public ImportFormat WithMappings(Func<ImportMapContainer, ImportMapContainer> config) =>
        WithImportFunction((_, dataSet, _, store) =>
            config(new(Workspace)).ImportAsync(dataSet, store)
        );

    public ImportFormat WithAutoMappings() => WithAutoMappingsForTypes(Workspace.MappedTypes);

    public ImportFormat WithValidation(ValidationFunction validationRule)
    {
        if (validationRule == null)
            return this;
        return this with { Validations = Validations.Add(validationRule) };
    }

    internal ImportFormat WithAutoMappingsForTypes(IReadOnlyCollection<Type> types) =>
        WithMappings(x => types.Aggregate(x, (a, b) => a.MapType(b)));

}

public delegate bool ValidationFunction(object instance, ValidationContext context, Activity activity);
