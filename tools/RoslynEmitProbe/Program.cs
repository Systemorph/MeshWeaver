using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Sources copied from core 174f5ab EmitPipeline's nested and flat controls.
const string nested = "public class MwEmitCanary<T> { public class Inner<U> { public class Leaf<V> { public T A; public U B; public V C; } } }";
const string flat = "public class MwFlatEmitCanary { }";
int Number(string flag, int fallback)
{
    var index = Array.IndexOf(args, flag);
    if (index < 0) return fallback;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value < 1)
        throw new ArgumentException($"{flag} requires a positive integer");
    return value;
}
var iterations = Number("--iterations", 1000);
var workers = Number("--workers", 1);
var negative = args.Contains("--invalid-source");
var corelib = typeof(object).Assembly.Location;
var shared = MetadataReference.CreateFromFile(corelib);
var counts = new ConcurrentDictionary<string, int>();
var details = new ConcurrentDictionary<string, object>();
var watch = Stopwatch.StartNew();
Console.WriteLine(JsonSerializer.Serialize(new
{
    kind = "environment", runtime = RuntimeInformation.FrameworkDescription,
    runtimeVersion = Environment.Version.ToString(), os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
    roslyn = typeof(CSharpCompilation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    roslynMvid = typeof(CSharpCompilation).Assembly.ManifestModule.ModuleVersionId,
    corelibMvid = typeof(object).Assembly.ManifestModule.ModuleVersionId,
    tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "unset",
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "unset",
    serverGc = System.Runtime.GCSettings.IsServerGC, iterations, workers, negative,
    note = "Standalone emit only: no MeshWeaver, DI scope, assembly loading, or collectible ALC."
}));

void Emit(string leg, string source, Func<MetadataReference> reference, int iteration, int worker, bool checkShape)
{
    string verdict;
    try
    {
        var compilation = CSharpCompilation.Create("MeshWeaverEmitCanary",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)], references: [reference()],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
            verdict = "diagnostics:" + string.Join(",", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).Distinct());
        else
        {
            // Verify that successful bytes contain the control's intended shape, without loading them.
            stream.Position = 0;
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var definitions = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
                .Where(t => metadata.GetString(t.Name) != "<Module>").ToArray();
            var nestedCount = definitions.Count(t => !t.GetDeclaringType().IsNil);
            var genericCount = definitions.Sum(t => t.GetGenericParameters().Count);
            var shapeOk = checkShape
                ? definitions.Length == 3 && nestedCount == 2 && genericCount == 6
                : definitions.Length == 1 && nestedCount == 0 && genericCount == 0;
            verdict = shapeOk ? "emits" : $"wrong-shape:{definitions.Length}/{nestedCount}/{genericCount}";
        }
    }
    catch (Exception error)
    {
        verdict = "throws:" + error.GetType().Name;
        details.TryAdd(leg + ":" + verdict, new { leg, iteration, worker, type = error.GetType().FullName, site = error.TargetSite?.ToString(), message = error.Message[..Math.Min(512, error.Message.Length)] });
    }
    counts.AddOrUpdate(leg + ":" + verdict, 1, (_, count) => count + 1);
    if (iteration == 0 || verdict != "emits")
        Console.WriteLine($"RESULT worker={worker} iteration={iteration} leg={leg} verdict={verdict}");
}

Parallel.For(0, workers, worker =>
{
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        var source = negative ? "public class {" : nested;
        Emit("shared-nested", source, () => shared, iteration, worker, true);
        Emit("shared-flat", flat, () => shared, iteration, worker, false);
        // Fresh owned CoreLib bytes: shares neither MetadataReference nor mapped image with shared leg.
        Emit("pristine-nested", source, () => MetadataReference.CreateFromImage(
            ImmutableArray.Create(File.ReadAllBytes(corelib))), iteration, worker, true);
        Emit("pristine-flat", flat, () => MetadataReference.CreateFromImage(
            ImmutableArray.Create(File.ReadAllBytes(corelib))), iteration, worker, false);
    }
});
var failures = counts.Where(kv => !kv.Key.EndsWith(":emits", StringComparison.Ordinal)).Sum(kv => kv.Value);
Console.WriteLine($"SUMMARY expectedAttempts={4L * iterations * workers} observedAttempts={counts.Values.Sum()} successfulEmits={counts.Where(kv => kv.Key.EndsWith(":emits", StringComparison.Ordinal)).Sum(kv => kv.Value)} failures={failures} elapsedSeconds={watch.Elapsed.TotalSeconds:F3}");
foreach (var pair in counts.OrderBy(kv => kv.Key)) Console.WriteLine($"COUNT {pair.Key}={pair.Value}");
foreach (var detail in details.Values) Console.WriteLine($"EXCEPTION {detail}");
Console.WriteLine(failures == 0
    ? "CONCLUSION No failure reproduced in this bounded standalone run; not a fix or proof of a lifecycle cause."
    : "CONCLUSION A failure was observed; inspect its leg and exception before attributing it to CI's failure.");
return failures == 0 ? 0 : 1;
