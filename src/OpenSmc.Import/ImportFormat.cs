using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Data;
using OpenSmc.DataStructures;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

public record ImportFormat(string Format, IMessageHub Hub, IWorkspace Workspace)
{
    public const string Default = nameof(Default);
    private ImmutableList<ImportFunction> ImportFunctions { get; init; } = ImmutableList<ImportFunction>.Empty;
    public ImmutableList<ValidationFunction> Validations { get; set; }

    public ImportFormat WithImportFunction(ImportFunction importFunction) =>
        this with { ImportFunctions = ImportFunctions.Add(importFunction) };

    public bool Import(ImportRequest importRequest, IDataSet dataSet,
        ImmutableList<ValidationFunction> defaultValidations, IDictionary<object, object> validationCache)
    {
        var updateOptions = new UpdateOptions().SnapshotMode(importRequest.SnapshotMode);
        bool hasError = false;

        foreach (var importFunction in ImportFunctions)
        {
            var importedInstances = importFunction(importRequest, dataSet, Hub, Workspace).ToArray();
            foreach (var item in importedInstances)
            {
                if (item == null)
                    continue;
                foreach (var validation in defaultValidations)
                    hasError = !validation(item, new ValidationContext(item, Hub.ServiceProvider, validationCache)) || hasError;
            }
            if (!hasError)
                Workspace.Update(importedInstances, updateOptions);
        }
        return hasError;
    }

    public delegate IEnumerable<object> ImportFunction(ImportRequest importRequest, IDataSet dataSet, IMessageHub hub, IWorkspace workspace);

    public ImportFormat WithAutoMappings()
        => WithAutoMappings(mapping => mapping);
    public ImportFormat WithAutoMappings(Func<DomainTypeImporter, DomainTypeImporter> config) 
        => WithImportFunction((_, ds, _, _) => config(new DomainTypeImporter(Workspace.DataContext)).Import(ds));
}

public delegate bool ValidationFunction(object instance, ValidationContext context);