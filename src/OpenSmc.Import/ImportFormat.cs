using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Data;
using OpenSmc.DataStructures;

namespace OpenSmc.Import;

public record ImportFormat(string Format, IWorkspace Workspace)
{
    public const string Default = nameof(Default);



    private ImmutableList<ImportFunction> ImportFunctions { get; init; } = ImmutableList<ImportFunction>.Empty;
    public ImmutableList<ValidationFunction> Validations { get; set; }

    public ImportFormat WithImportFunction(ImportFunction importFunction) =>
        this with { ImportFunctions = ImportFunctions.Add(importFunction) };

    public void Import(ImportRequest importRequest, IDataSet dataSet)
    {
        foreach (var importFunction in ImportFunctions)
            Workspace.Update(importFunction(importRequest, dataSet, Workspace).ToArray());
    }

    public delegate IEnumerable<object> ImportFunction(ImportRequest importRequest, IDataSet dataSet, IWorkspace workspace);

    public ImportFormat WithAutoMappings()
        => WithAutoMappings(mapping => mapping);
    public ImportFormat WithAutoMappings(Func<DomainTypeImporter, DomainTypeImporter> config) 
        => WithImportFunction(config(new DomainTypeImporter(Workspace.DataContext)).Import);
}

public delegate bool ValidationFunction(object instance, ValidationContext context);