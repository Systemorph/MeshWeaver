using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.DataStructures;
using MeshWeaver.Domain;

namespace MeshWeaver.Import.Configuration;

public record ImportFormat(
    string Format,
    IWorkspace Workspace,
    IReadOnlyCollection<Type> MappedTypes,
    ImmutableList<ValidationFunction> Validations)
{
    public const string Default = nameof(Default);

    private ImmutableList<ImportFunction> ImportFunctions { get; init; } =
        ImmutableList<ImportFunction>.Empty;

    public ImportFormat WithImportFunction(ImportFunction importFunction) =>
        this with
        {
            ImportFunctions = ImportFunctions.Add(importFunction)
        };


    public WorkspaceReference<EntityStore> GetReference(IDataSet dataSet)
        => WorkspaceReferenceMapping.Invoke(dataSet);

    private Func<IDataSet, WorkspaceReference<EntityStore>> WorkspaceReferenceMapping { get; init; }
        = dataSet => new CollectionsReference(dataSet.Tables
            .Select(t => MappedTypes.FirstOrDefault(x => x.Name == t.TableName))
            .Where(x => x != null)
            .SelectMany(t => new Type[]{t}.Concat(t.GetProperties().Select(p => p.GetCustomAttribute<DimensionAttribute>()?.Type).Where(t => t != null)))
            .Select(x => Workspace.DataContext.TypeRegistry.GetCollectionName(x))
            .Where(x => x != null)
            .ToArray());

    public ImportFormat WithWorkspaceReference(Func<IDataSet, WorkspaceReference<EntityStore>> mapping)
    => this with { WorkspaceReferenceMapping = mapping };
    public EntityStoreAndUpdates Import(
        ImportRequest importRequest,
        IDataSet dataSet,
        EntityStore store,
        Activity<ChangeItem<EntityStore>> activity
    )
    {
        var hasError = false;
        var validationCache = new Dictionary<object, object>();
        EntityStoreAndUpdates storeAndUpdates = new(store);
        foreach (var importFunction in ImportFunctions)
        {

            var importedInstances = importFunction(importRequest, dataSet, Workspace, storeAndUpdates.Store).ToArray();
            foreach (var item in importedInstances)
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



            var newStore = storeAndUpdates.Store.MergeWithUpdates(new EntityStore(importedInstances
                .GroupBy(g => g.GetType()).Select(g =>
                {
                    var type = g.Key;
                    var keyFunction = Workspace.DataContext.TypeRegistry.GetKeyFunction(type)?.Function;
                    var collection = Workspace.DataContext.TypeRegistry.GetCollectionName(g.Key);
                    if (keyFunction == null || collection == null)
                        return default;
                    return new KeyValuePair<string, InstanceCollection>(collection,
                        new InstanceCollection(g.ToDictionary(keyFunction)));
                }).ToDictionary()), importRequest.UpdateOptions);

            storeAndUpdates = new EntityStoreAndUpdates(Store: newStore.Store, Changes: storeAndUpdates.Changes.Concat(newStore.Changes));

        }
        return storeAndUpdates;
    }

    public delegate IEnumerable<object> ImportFunction(
        ImportRequest importRequest,
        IDataSet dataSet,
        IWorkspace workspace,
        EntityStore state
    );

    public ImportFormat WithMappings(Func<ImportMapContainer, ImportMapContainer> config) =>
        WithImportFunction((_, dataSet, _, _) => config(new()).Import(dataSet));

    public ImportFormat WithAutoMappings() => WithAutoMappingsForTypes(MappedTypes);

    public ImportFormat WithValidation(ValidationFunction validationRule)
    {
        if (validationRule == null)
            return this;
        return this with { Validations = Validations.Add(validationRule) };
    }

    internal ImportFormat WithAutoMappingsForTypes(IReadOnlyCollection<Type> types) =>
        WithMappings(x => types.Aggregate(x, (a, b) => a.MapType(b)));

}

public delegate bool ValidationFunction(object instance, ValidationContext context, Activity<ChangeItem<EntityStore>> activity);
