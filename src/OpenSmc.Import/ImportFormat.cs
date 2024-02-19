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

    public void Import(ImportRequest importRequest, IDataSet dataSet)
    {
        var updateOptions = new UpdateOptions().SnapshotMode(importRequest.SnapshotMode);
        foreach (var importFunction in ImportFunctions)
            Workspace.Update(importFunction(importRequest, dataSet, Hub, Workspace).ToArray(), updateOptions);
    }

    public delegate IEnumerable<object> ImportFunction(ImportRequest importRequest, IDataSet dataSet, IMessageHub hub, IWorkspace workspace);

    public ImportFormat WithAutoMappings()
        => WithAutoMappings(mapping => mapping);
    public ImportFormat WithAutoMappings(Func<DomainTypeImporter, DomainTypeImporter> config) 
        => WithImportFunction((_, ds, _, _) => config(new DomainTypeImporter(Workspace.DataContext)).Import(ds));
}

public delegate bool ValidationFunction(object instance, ValidationContext context);