using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Data;
using OpenSmc.DataStructures;

namespace OpenSmc.Import;

public record ImportFormat(string Format)
{
    public const string Default = nameof(Default);



    private ImmutableList<ImportFunction> ImportFunctions { get; init; } = ImmutableList<ImportFunction>.Empty;
    public ImmutableList<ValidationFunction> Validations { get; set; }

    public ImportFormat WithImportFunction(ImportFunction importFunction) =>
        this with { ImportFunctions = ImportFunctions.Add(importFunction) };

    public IReadOnlyCollection<object> Import(ImportRequest importRequest, IDataSet dataSet)
        => ImportFunctions.Select(f => f.Invoke(importRequest, dataSet)).Aggregate((x,y) => x.Concat(y)).ToArray();

    public delegate IEnumerable<object> ImportFunction(ImportRequest importRequest, IDataSet dataSet);

    public ImportFormat WithAutoMappings(DataContext dataContext, Func<DomainTypeImporter, DomainTypeImporter> config)
    {
        return this
            .WithImportFunction(config(new DomainTypeImporter(dataContext)).Import);
    }

}

public delegate bool ValidationFunction(object instance, ValidationContext context);