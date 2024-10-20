using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Data;
using MeshWeaver.DataStructures;
using Activity = MeshWeaver.Activities.Activity;

namespace MeshWeaver.Import.Configuration;

public record ImportFormat(
    string Format,
    IWorkspace Workspace,
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



    public async Task<IReadOnlyCollection<object>> Import(
        ImportRequest importRequest,
        IDataSet dataSet,
        Activity activity
    )
    {
        var imported = await ImportFunctions
            .Select(i => i.Invoke(importRequest, dataSet, Workspace))
            .Aggregate(AsyncEnumerable.Empty<object>(), (e, r) => e.Concat(r))
            .ToArrayAsync();

        Validate(imported, activity);
        if (activity.HasErrors())
            return null;
        return imported;
    }

    private void Validate(IReadOnlyCollection<object> importedInstances, Activity activity)
    {
        var hasError = false;
        var validationCache = new Dictionary<object, object>();
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

    }

    public delegate IAsyncEnumerable<object> ImportFunction(
        ImportRequest importRequest,
        IDataSet dataSet,
        IWorkspace workspace
    );

    public ImportFormat WithMappings(Func<ImportMapContainer, ImportMapContainer> config) =>
        WithImportFunction((_, dataSet, _) =>
            config(new()).Import(dataSet)
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
