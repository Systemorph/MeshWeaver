﻿using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Data;
using MeshWeaver.DataStructures;

namespace MeshWeaver.Import.Configuration;

public record ImportFormat(
    string Format,
    IWorkspace Workspace,
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
            var importedInstances = importFunction(importRequest, dataSet, Workspace, state).ToArray();
            foreach (var item in importedInstances)
            {
                if (item == null)
                    continue;
                foreach (var validation in Validations)
                    hasError =
                        !validation(
                            item,
                            new ValidationContext(item, Workspace.Hub.ServiceProvider, validationCache)
                        ) || hasError;
            }
            state = state.Update(importedInstances, updateOptions);
        }
        return (state, hasError);
    }

    public delegate IEnumerable<object> ImportFunction(
        ImportRequest importRequest,
        IDataSet dataSet,
        IWorkspace workspace,
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
