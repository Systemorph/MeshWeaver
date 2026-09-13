using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.Compiler;

/// <summary>
/// Leg 5 of the #890 canary — the PRISTINE-COMPILER control. Loads <c>Microsoft.CodeAnalysis</c> and
/// <c>Microsoft.CodeAnalysis.CSharp</c> a SECOND time, from freshly read bytes, into a collectible
/// <see cref="AssemblyLoadContext"/> that shares nothing with the copy every compilation in the
/// process has been running through — not its statics, not its caches, not its JIT'd code — and
/// drives the canary source through that private copy by reflection.
///
/// <para>🚨 <b>Why the other four legs cannot answer this.</b> Every occurrence of #890 has read
/// the same way: the shared reference set cannot emit (leg 1), an image-backed pristine reference
/// set cannot emit either (leg 2), the symbol graph reads correctly (<c>dissect=READS-HEALTHY</c>,
/// leg 3), and the flat source sometimes emits and sometimes dies in the same frame (leg 4). All
/// four execute the SAME <c>Microsoft.CodeAnalysis*.dll</c> — the same mapped image, the same
/// native code, the same statics — so none of them separates "the process cannot emit" from "the
/// process's ONE copy of Roslyn cannot emit". <c>BELOW-ROSLYN</c> has been reading "not the
/// references" as "not Roslyn", which is the inference this thread has caught three times. This
/// leg is the control for exactly that: a copy of the compiler that has never executed in this
/// process, run on the same source, in the same process, on the same CLR.</para>
///
/// <para><b>What the two answers mean.</b>
/// <list type="bullet">
///   <item><c>compiler=PRIVATE-COPY-EMITS</c> — a never-before-executed Roslyn emits the same
///     source that the shared one cannot ⇒ the broken state lives IN the shared copy (a static, a
///     cache, a miscompiled method), not below it. The remedy is to scope the shared compiler out
///     (a per-generation compiler context), and a <c>dotnet/runtime</c> report is NOT what the
///     evidence supports.</item>
///   <item><c>compiler=PRIVATE-COPY-THREW … at …</c> — even fresh compiler code cannot emit ⇒ the
///     process cannot emit at all; <c>BELOW-ROSLYN</c> is earned, and the throwing frame of the
///     private copy is the reproduction to file.</item>
///   <item><c>compiler=PRIVATE-COPY-UNAVAILABLE(…)</c> — the leg could not RUN (a single-file host
///     with no on-disk Roslyn image, a load context that handed the shared assembly back, a Roslyn
///     shape this reflection does not know). Stated, never folded into either verdict above — a
///     control that reports its own inability as an answer is the gate-that-passes-on-missing-input
///     defect.</item>
/// </list></para>
///
/// <para><b>Cost and reach.</b> Two assembly loads (~15 MB of bytes read, two images), one tiny
/// emit, one <see cref="AssemblyLoadContext.Unload"/> — only ever on the already-failing terminal
/// path that runs the other legs, never on a success or an ordinary compile error. The private
/// copy's OTHER dependencies (<c>System.Collections.Immutable</c>, <c>System.Reflection.Metadata</c>,
/// the framework) resolve to the default context on purpose: they are the same for both copies,
/// and varying them would make the control answer a question nobody asked.</para>
/// </summary>
internal static class PrivateRoslynCopy
{
    /// <summary>The verdict token every outcome of this leg starts with.</summary>
    internal const string Prefix = "compiler=";

    /// <summary>
    /// Emits <paramref name="source"/> through a freshly loaded private copy of Roslyn and reports
    /// the outcome as a one-line verdict. Never throws.
    /// </summary>
    /// <param name="source">The source to compile — the canary's, by default.</param>
    /// <returns>A verdict starting with <see cref="Prefix"/>.</returns>
    internal static string Emit(string source)
    {
        var sharedCore = typeof(Compilation).Assembly;
        var sharedCSharp = typeof(CSharpCompilation).Assembly;
        if (string.IsNullOrEmpty(sharedCore.Location) || !File.Exists(sharedCore.Location)
            || string.IsNullOrEmpty(sharedCSharp.Location) || !File.Exists(sharedCSharp.Location))
            return $"{Prefix}PRIVATE-COPY-UNAVAILABLE(no on-disk image for the shared Roslyn assemblies — "
                + "a single-file host; the private copy cannot be read)";

        var coreLib = typeof(object).Assembly.Location;
        if (string.IsNullOrEmpty(coreLib) || !File.Exists(coreLib))
            return $"{Prefix}PRIVATE-COPY-UNAVAILABLE(no on-disk System.Private.CoreLib to reference)";

        var context = new PrivateRoslynLoadContext();
        try
        {
            var core = context.LoadPrivate(sharedCore.GetName().Name!, File.ReadAllBytes(sharedCore.Location));
            var csharp = context.LoadPrivate(sharedCSharp.GetName().Name!, File.ReadAllBytes(sharedCSharp.Location));
            if (ReferenceEquals(core, sharedCore) || ReferenceEquals(csharp, sharedCSharp)
                || ReferenceEquals(AssemblyLoadContext.GetLoadContext(csharp), AssemblyLoadContext.Default))
                return $"{Prefix}PRIVATE-COPY-UNAVAILABLE(the load context handed the SHARED assembly back — "
                    + "the control would have executed the same code it is meant to control for)";

            return EmitThrough(core, csharp, source, coreLib);
        }
        catch (TargetInvocationException reflective) when (reflective.InnerException is { } inner)
        {
            return $"{Prefix}PRIVATE-COPY-THREW {inner.GetType().Name} at {EmitPipeline.ThrowSite(inner)}: {inner.Message}";
        }
        catch (Exception probeError)
        {
            return $"{Prefix}PRIVATE-COPY-UNAVAILABLE({probeError.GetType().Name} at "
                + $"{EmitPipeline.ThrowSite(probeError)}: {probeError.Message})";
        }
        finally
        {
            try { context.Unload(); }
            catch { /* best-effort: a copy that will not unload costs memory, not correctness */ }
        }
    }

    /// <summary>
    /// Reads the verdict <see cref="Emit"/> produced as the <c>compiler=</c> reading of a
    /// canary verdict line — the same rendering the other legs get (<c>dissect=</c>, <c>flat=</c>).
    /// </summary>
    internal static string Render(Func<string>? compiler)
    {
        if (compiler is null)
            return $"{Prefix}NOT-RUN (no probe supplied)";
        try
        {
            return compiler();
        }
        catch (Exception probeError)
        {
            return $"{Prefix}PRIVATE-COPY-UNAVAILABLE({probeError.GetType().Name} — the probe itself faulted, "
                + "so it says nothing either way)";
        }
    }

    private static string EmitThrough(Assembly core, Assembly csharp, string source, string coreLibPath)
    {
        // Every type below is the PRIVATE copy's — resolved from the two assemblies just loaded,
        // never from the shared copy's compile-time references — which is the whole point.
        var syntaxTreeType = Required(core, "Microsoft.CodeAnalysis.SyntaxTree");
        var metadataReferenceType = Required(core, "Microsoft.CodeAnalysis.MetadataReference");
        var outputKindType = Required(core, "Microsoft.CodeAnalysis.OutputKind");
        var csharpSyntaxTreeType = Required(csharp, "Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
        var csharpCompilationType = Required(csharp, "Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
        var csharpOptionsType = Required(csharp, "Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");

        // CSharpSyntaxTree.ParseText(string text, …defaults…)
        var parse = PublicStatic(csharpSyntaxTreeType, "ParseText", first: typeof(string));
        var tree = InvokeWithDefaults(parse, null, source)!;

        // MetadataReference.CreateFromImage(IEnumerable<byte> peImage, …defaults…) — an IMAGE-backed
        // CoreLib reference, fresh bytes, exactly as leg 2 builds its pristine control.
        var createFromImage = PublicStatic(metadataReferenceType, "CreateFromImage", first: typeof(IEnumerable<byte>));
        var coreLibReference = InvokeWithDefaults(createFromImage, null, File.ReadAllBytes(coreLibPath))!;

        // new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, …defaults…)
        var outputKind = Enum.Parse(outputKindType, nameof(OutputKind.DynamicallyLinkedLibrary));
        var optionsCtor = csharpOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters() is { Length: > 0 } ps && ps[0].ParameterType == outputKindType)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var options = optionsCtor.Invoke(WithDefaults(optionsCtor.GetParameters(), outputKind));

        // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, options)
        var trees = Array.CreateInstance(syntaxTreeType, 1);
        trees.SetValue(tree, 0);
        var references = Array.CreateInstance(metadataReferenceType, 1);
        references.SetValue(coreLibReference, 0);
        var create = PublicStatic(csharpCompilationType, "Create", first: typeof(string));
        var compilation = InvokeWithDefaults(create, null, "MeshWeaverPrivateRoslynCanary", trees, references, options)!;

        // compilation.Emit(Stream peStream, …defaults…)
        var emit = compilation.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Emit" && m.GetParameters() is { Length: > 0 } ps && ps[0].ParameterType == typeof(Stream))
            .OrderBy(m => m.GetParameters().Length)
            .First();
        using var peStream = new MemoryStream();
        var result = InvokeWithDefaults(emit, compilation, peStream)!;

        var success = (bool)result.GetType().GetProperty("Success")!.GetValue(result)!;
        if (success)
            return $"{Prefix}PRIVATE-COPY-EMITS — a copy of Roslyn loaded from fresh bytes into its own "
                + "collectible context, never executed in this process before, emits the same source in "
                + "the same process on the same CLR ⇒ the broken state lives IN the shared compiler (a "
                + "static, a cache, a miscompiled method), not below it; scope the shared compiler out "
                + "per generation, and do not file this against the runtime";

        var ids = new List<string>();
        if (result.GetType().GetProperty("Diagnostics")?.GetValue(result) is IEnumerable diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                var severity = diagnostic.GetType().GetProperty("Severity")?.GetValue(diagnostic)?.ToString();
                if (severity != nameof(DiagnosticSeverity.Error))
                    continue;
                var id = diagnostic.GetType().GetProperty("Id")?.GetValue(diagnostic)?.ToString();
                if (id is not null && !ids.Contains(id))
                    ids.Add(id);
                if (ids.Count == 5)
                    break;
            }
        }
        return $"{Prefix}PRIVATE-COPY-DIAGNOSTICS({string.Join(",", ids)}) — the private copy compiled the "
            + "canary and REFUSED it with diagnostics rather than throwing, which no other leg has "
            + "produced for this source; read the ids before drawing anything from it";
    }

    private static Type Required(Assembly assembly, string fullName)
        => assembly.GetType(fullName, throwOnError: false)
           ?? throw new MissingMemberException($"{fullName} is not in the private {assembly.GetName().Name}");

    private static MethodInfo PublicStatic(Type type, string name, Type first)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
               .Where(m => m.Name == name && m.GetParameters() is { Length: > 0 } ps && ps[0].ParameterType == first)
               .OrderBy(m => m.GetParameters().Length)
               .FirstOrDefault()
           ?? throw new MissingMethodException(type.FullName, name);

    /// <summary>Invokes <paramref name="method"/> with <paramref name="leading"/> and every remaining
    /// parameter at its declared default — the private copy's optional-parameter surface, honoured
    /// without naming any of it.</summary>
    private static object? InvokeWithDefaults(MethodInfo method, object? target, params object?[] leading)
        => method.Invoke(target, WithDefaults(method.GetParameters(), leading));

    private static object?[] WithDefaults(ParameterInfo[] parameters, params object?[] leading)
    {
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i < leading.Length)
            {
                args[i] = leading[i];
                continue;
            }
            var p = parameters[i];
            args[i] = p.HasDefaultValue
                ? p.DefaultValue is null && p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null
                    ? Activator.CreateInstance(p.ParameterType)
                    : p.DefaultValue
                : p.ParameterType.IsValueType
                    ? Activator.CreateInstance(p.ParameterType)
                    : null;
        }
        return args;
    }

    /// <summary>
    /// A collectible context that serves the two private Roslyn assemblies to each other and
    /// nothing else: <c>Microsoft.CodeAnalysis.CSharp</c>'s reference to <c>Microsoft.CodeAnalysis</c>
    /// resolves to the private core, every other dependency falls through to the default context.
    /// </summary>
    private sealed class PrivateRoslynLoadContext() : AssemblyLoadContext("MeshWeaverPrivateRoslyn", isCollectible: true)
    {
        private readonly Dictionary<string, Assembly> own = new(StringComparer.OrdinalIgnoreCase);

        public Assembly LoadPrivate(string simpleName, byte[] image)
        {
            using var stream = new MemoryStream(image, writable: false);
            var assembly = LoadFromStream(stream);
            own[simpleName] = assembly;
            return assembly;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
            => assemblyName.Name is { } name && own.TryGetValue(name, out var assembly) ? assembly : null;
    }
}
