using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Data;
using OpenSmc.DataStructures;
using OpenSmc.Messaging;

namespace OpenSmc.Import.Configuration;

public record ImportFormat(
    string Format,
    IMessageHub Hub,
    IReadOnlyCollection<Type> MappedTypes,
    ImmutableList<ValidationFunction> Validations
)
{
    public const string Default = nameof(Default);
    private ImmutableList<ImportFunction> ImportFunctions { get; init; } =
        ImmutableList<ImportFunction>.Empty;

    public ImportFormat WithImportFunction(ImportFunction importFunction) =>
        this with
        {
            ImportFunctions = ImportFunctions.Add(importFunction)
        };

    public (WorkspaceState State, bool hasError) Import(
        ImportRequest importRequest,
        IDataSet dataSet,
        WorkspaceState state
    )
    {
        var updateOptions = new UpdateOptions { Snapshot = importRequest.Snapshot };
        bool hasError = false;
        var validationCache = new Dictionary<object, object>();

        foreach (var importFunction in ImportFunctions)
        {
            var importedInstances = importFunction(importRequest, dataSet, Hub, state).ToArray();
            foreach (var item in importedInstances)
            {
                if (item == null)
                    continue;
                foreach (var validation in Validations)
                    hasError =
                        !validation(
                            item,
                            new ValidationContext(item, Hub.ServiceProvider, validationCache)
                        ) || hasError;
            }
            state = state.Update(importedInstances, updateOptions);
        }
        return (state, hasError);
    }

    public delegate IEnumerable<object> ImportFunction(
        ImportRequest importRequest,
        IDataSet dataSet,
        IMessageHub hub,
        WorkspaceState state
    );

    public ImportFormat WithMappings(Func<ImportMapContainer, ImportMapContainer> config) =>
        WithImportFunction((request, dataSet, hub, state) => config(new()).Import(dataSet));

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

public delegate bool ValidationFunction(object instance, ValidationContext context);
