using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// One REPL session's Roslyn script chain, with every submission assembly loaded into a
/// single <b>collectible</b> <see cref="AssemblyLoadContext"/> owned by the session — so the
/// per-cell assemblies actually unload when the session dies, instead of accumulating for the
/// process lifetime.
///
/// <para>🚨 Why not <c>CSharpScript.RunAsync</c>/<c>ScriptState.ContinueWithAsync</c>: Roslyn's
/// scripting host loads every submission through an <c>InteractiveAssemblyLoader</c> whose
/// internal <c>LoadContext</c> is created NON-collectible (verified against Roslyn 5.3) — the
/// emitted assembly per <c>--render</c> cell is permanent even after the loader is disposed.
/// That is the portal's memory-fatigue wall: a long compile/execute run climbs monotonically
/// until GC/thread-pool stalls kill it. So this class keeps Roslyn for what it is good at —
/// building the submission <em>compilation chain</em> via <c>CSharpScript.Create</c> /
/// <see cref="Script.ContinueWith(string, ScriptOptions)"/> — and owns emit + load + invoke
/// itself, replicating the scripting host's submission protocol: slot 0 of the state array is
/// the globals instance, slot N+1 is submission #N's instance, and each submission's generated
/// <c>&lt;Factory&gt;(object[])</c> reads its predecessors from and writes itself into that
/// array. REPL semantics (block #2 sees block #1's variables, functions, and types) are
/// preserved — pinned by <c>KernelReplChainingTest</c>.</para>
///
/// <para>🚨 The load context's submission map holds <see cref="WeakReference{T}"/>s, never
/// strong <see cref="Assembly"/> references: a collectible context that strongly references its
/// own assemblies creates an uncollectable cycle THROUGH A GC HANDLE (LoaderAllocator →
/// strong handle → context object → Assembly → LoaderAllocator) and never unloads — the exact
/// pin this class exists to remove. Weak is sufficient: a live context roots its own
/// assemblies natively, so the target can only be collected once the whole context is.</para>
///
/// <para>Not thread-safe by design — all calls are serialized by the executor's Concat pump.
/// <see cref="Dispose"/> initiates an eager unload; unloading is cooperative, so a script
/// still executing (or a script-created object still referenced by a live layout area) keeps
/// the context alive until those references die, then everything is reclaimed. Even without
/// Dispose, an abandoned session's context island is garbage-collected once unreferenced —
/// the weak map is what makes that possible.</para>
/// </summary>
internal sealed class ScriptSession(object globals) : IDisposable
{
    /// <summary>The session context's <see cref="AssemblyLoadContext.Name"/> — asserted gone
    /// after mesh disposal by <c>KernelScriptMemoryLeakTest</c>.</summary>
    public const string LoadContextName = "kernel-script-session";

    private readonly SessionLoadContext loadContext = new();
    private readonly List<object?> submissionStates = [globals];
    private Script<object>? previousScript;
    private bool disposed;

    /// <summary>
    /// Compiles <paramref name="code"/> as the next submission of this session's chain, loads
    /// it into the session's collectible context, and executes it. Returns the submission's
    /// return value (the last expression, or null). Compilation errors throw
    /// <see cref="CompilationErrorException"/> — the same contract as
    /// <c>CSharpScript.RunAsync</c>. A submission that throws at RUNTIME does not advance the
    /// chain (its variables are discarded), matching <c>ScriptState</c> semantics.
    /// </summary>
    public async Task<object?> RunAsync(string code, ScriptOptions options, Type globalsType, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var script = previousScript is null
            ? CSharpScript.Create(code, options, globalsType)
            : previousScript.ContinueWith(code, options);
        var compilation = script.GetCompilation();

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emitted = compilation.Emit(
            peStream,
            pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: ct);
        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
            throw new CompilationErrorException(
                string.Join(Environment.NewLine, errors.Select(d => d.ToString())),
                errors.IsEmpty ? emitted.Diagnostics : errors);
        }

        var assembly = loadContext.LoadSubmission(compilation.AssemblyName!, peStream, pdbStream);

        // The scripting host's submission protocol: the generated static
        // Task<object> <Factory>(object[] submissionStates) reads predecessors from the array
        // (slot 0 = globals, slot i+1 = submission #i) and writes its own instance into its slot.
        var scriptClass = compilation.ScriptClass!.MetadataName;
        var factory = assembly.GetType(scriptClass, throwOnError: true)!
                          .GetMethod("<Factory>", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                      ?? throw new InvalidOperationException($"Submission type '{scriptClass}' has no <Factory> entry point.");
        var submissionIndex = SubmissionIndex(scriptClass);
        while (submissionStates.Count < submissionIndex + 2)
            submissionStates.Add(null);
        var states = submissionStates.ToArray();

        // Await the factory directly — no WaitAsync(ct): a running submission is never
        // abandoned mid-flight (parity with ScriptState.RunAsync); cancellation reaches the
        // script through its own globals.Ct at its await points, exactly as before.
        var returnValue = await factory.CreateDelegate<Func<object[], Task<object>>>()(states!)
            .ConfigureAwait(false);

        // Advance the chain only on success — a throwing submission's variables are discarded,
        // exactly like ScriptState (its orphaned assembly stays in the context until unload).
        for (var i = 0; i < states.Length; i++)
            submissionStates[i] = states[i];
        previousScript = script;
        return returnValue;
    }

    // "Submission#0" → 0. The generated script-class name is the submission protocol's
    // load-bearing index; failing loudly beats guessing a slot.
    private static int SubmissionIndex(string scriptClassMetadataName)
    {
        var hash = scriptClassMetadataName.LastIndexOf('#');
        if (hash < 0 || !int.TryParse(scriptClassMetadataName[(hash + 1)..], out var index))
            throw new InvalidOperationException(
                $"Unexpected submission type name '{scriptClassMetadataName}' — cannot derive the submission slot.");
        return index;
    }

    /// <summary>Eagerly initiates the context unload and drops the chain so the session's
    /// assemblies can be reclaimed. Safe while a script is still running (cooperative).</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        previousScript = null;
        submissionStates.Clear();
        loadContext.Unload();
    }

    private sealed class SessionLoadContext() : AssemblyLoadContext(LoadContextName, isCollectible: true)
    {
        // 🚨 WeakReference values — see the class doc; a strong Assembly ref here recreates the
        // GC-handle cycle that makes the context permanent.
        private readonly Dictionary<string, WeakReference<Assembly>> submissions = [];

        public Assembly LoadSubmission(string assemblyName, MemoryStream pe, MemoryStream pdb)
        {
            pe.Position = 0;
            pdb.Position = 0;
            var assembly = LoadFromStream(pe, pdb);
            submissions[assemblyName] = new WeakReference<Assembly>(assembly);
            return assembly;
        }

        // Later submissions reference earlier ones by assembly identity; everything else falls
        // through (null) to the default context — host assemblies, NuGet-restored packages, …
        protected override Assembly? Load(AssemblyName assemblyName)
            => assemblyName.Name is not null
               && submissions.TryGetValue(assemblyName.Name, out var weak)
               && weak.TryGetTarget(out var assembly)
                ? assembly
                : null;
    }
}
