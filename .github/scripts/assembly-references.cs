// Prints the AssemblyRef table of every assembly named on the command line, one
// "<file name>\t<referenced simple name>" line per reference, and "<file name>\t" for a file that
// references nothing (or carries no metadata — a native payload that slipped in by name).
//
// Used by module-required-closure.py, which copies this file into an empty temporary directory and
// runs it with `dotnet run --file`: a file-based app picks up every Directory.Build.props above it,
// and the platform's would turn it into a different program.
//
// Metadata only — nothing is loaded, so a reference the runner cannot resolve costs nothing here.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

foreach (var path in args)
{
    var file = Path.GetFileName(path);
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata)
    {
        Console.WriteLine($"{file}\t");
        continue;
    }
    var metadata = pe.GetMetadataReader();
    var names = metadata.AssemblyReferences
        .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
        .Distinct(StringComparer.Ordinal)
        .ToList();
    if (names.Count == 0)
        Console.WriteLine($"{file}\t");
    foreach (var name in names)
        Console.WriteLine($"{file}\t{name}");
}
