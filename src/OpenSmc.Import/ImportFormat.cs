using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Data;
using OpenSmc.DataStructures;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

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

    public ImportFormat WithAutoMappingsForTypes(IReadOnlyCollection<Type> types) =>
        WithImportFunction(
            (_, ds, _, _) =>
                new DomainTypeImporter { TableMappings = AutoMapper.Create(types) }.Import(ds)
        );

    public ImportFormat WithAutoMappings() => WithAutoMappingsForTypes(MappedTypes);

    public ImportFormat WithMappings(Func<DomainTypeImporter, DomainTypeImporter> config) =>
        WithImportFunction((_, ds, _, _) => config(new DomainTypeImporter()).Import(ds));

    public ImportFormat WithValidation(ValidationFunction validationRule)
    {
        if (validationRule == null)
            return this;
        return this with { Validations = Validations.Add(validationRule) };
    }
}

public delegate bool ValidationFunction(object instance, ValidationContext context);
