using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// The actual kernel runtime, hosted as a child hub of <see cref="KernelContainer"/>.
/// Owns the per-session Roslyn <see cref="ScriptState{TResult}"/> so subsequent
/// submissions extend a single REPL (variables defined in submission #1 are
/// visible to submission #2). Runs scripts on its own action block + thread-pool
/// continuations, so the parent hub is never blocked while a script is executing.
///
/// <para>External clients never address this hub directly — see <see cref="KernelContainer"/>
/// docs.</para>
/// </summary>
internal sealed class KernelExecutor(IMessageHub publicHub)
{
    private ScriptState<object>? scriptState;
    private ScriptOptions scriptOptions = ScriptOptions.Default;
    private MeshScriptGlobals? scriptGlobals;
    private ScriptLogger? scriptLogger;
    private bool initialized;

    private readonly SemaphoreSlim executionLock = new(1, 1);

    // 🚦 Roslyn script compile+execute is a CPU/blocking leaf (the compile prologue +
    // RuntimeMetadataReferenceResolver.ResolveMissingAssembly file I/O run synchronously).
    // It MUST go through the bounded Compile IoPool — NEVER a bare Observable.FromAsync on
    // the shared ThreadPool. An unbounded script compile parks a ThreadPool thread for the
    // whole compile (and can block on an assembly-file lock when a concurrent NodeType
    // compile is writing the same DLL); a burst of them under a bulk test run starves the
    // ThreadPool, so every reactive-timeout op elsewhere (synced queries, mesh-node updates)
    // deadlocks. Routing through the SAME Compile pool NodeType compilation uses serialises
    // the two so they never race on the same assembly file. Unbounded fallback only when no
    // registry is wired (DI-less); still offloads, just no cap.
    private readonly IIoPool compilePool =
        publicHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Compile)
            ?? IoPool.Unbounded;

    // NuGet restore (#r "nuget:...") is a genuine network + file-system I/O leaf —
    // route it through the bounded Http pool, never a bare Observable.FromAsync.
    private readonly IIoPool nugetPool =
        publicHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
            ?? IoPool.Unbounded;

    // Cancellation source for the script currently inside ExecuteAsync. Replaced
    // on each submission. CancelScriptRequest cancels it; the script's
    // CancellationToken trips at the next await point, RunAsync/ContinueWithAsync
    // throws OperationCanceledException, the activity log flips to Failed.
    private CancellationTokenSource? activeCancellation;
    private readonly object cancellationLock = new();

    private ILogger Logger => publicHub.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<KernelExecutor>();

    public MessageHubConfiguration Configure(MessageHubConfiguration config)
    {
        config.TypeRegistry.WithType(typeof(SubmitCodeRequest), nameof(SubmitCodeRequest));
        config.TypeRegistry.WithType(typeof(SubmitCodeResponse), nameof(SubmitCodeResponse));
        config.TypeRegistry.WithType(typeof(CancelScriptRequest), nameof(CancelScriptRequest));
        return config
            .WithHandler<SubmitCodeRequest>(HandleSubmitCodeRequest)
            .WithHandler<CancelScriptRequest>(HandleCancelRequest);
    }

    private IMessageDelivery HandleCancelRequest(IMessageHub _, IMessageDelivery<CancelScriptRequest> request)
    {
        lock (cancellationLock)
        {
            activeCancellation?.Cancel();
        }
        return request.Processed();
    }

    /// <summary>
    /// Sync entry — composes the script work as <see cref="IObservable{T}"/> end
    /// to end so the executor's action block isn't blocked while the script runs.
    /// Concurrency on <see cref="scriptState"/> is serialised by <see cref="executionLock"/>;
    /// the lock is acquired through <see cref="Observable.FromAsync(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task})"/>
    /// at the SemaphoreSlim boundary, every other step is plain Rx composition.
    /// </summary>
    private IMessageDelivery HandleSubmitCodeRequest(IMessageHub hub, IMessageDelivery<SubmitCodeRequest> request)
    {
        var msg = request.Message;
        // ActivityLogPath defaults to the public hub's address — the hub IS the
        // activity in the common case. Caller can override for cross-hub logging.
        var activityPath = !string.IsNullOrEmpty(msg.ActivityLogPath)
            ? msg.ActivityLogPath!
            : publicHub.Address.ToString();
        var activityLogger = new ActivityLogLogger(hub, activityPath);

        // Pair each submission with its own CancellationTokenSource. The handler
        // for CancelScriptRequest cancels whatever's currently active.
        var cts = new CancellationTokenSource();
        lock (cancellationLock) { activeCancellation = cts; }

        // 🚨 Subscribe INLINE on the action block — do NOT SubscribeOn(TaskPool).
        // This is a REPL: submissions must execute in the order they were posted
        // (block #1 defines `x`, block #2 reads it). The action block delivers
        // SubmitCodeRequests FIFO, and callers fire them back-to-back without
        // awaiting each response (see MonolithKernelTest.ThreeSubmissions_ShareState),
        // so submission order is established ONLY by the order in which each
        // pipeline acquires `executionLock`. Subscribing here, on the action
        // block, calls executionLock.WaitAsync(ct) synchronously and in order, so
        // the semaphore queues the waiters FIFO. SubscribeOn(TaskPool) instead
        // scheduled the whole subscription — including the WaitAsync call — onto
        // unordered pool threads, letting block #2 win the lock before block #1
        // and run against an empty ScriptState (CS0103 "name does not exist").
        // Execute() offloads the heavy work (EnsureInitialized's AppDomain scan +
        // the cold Roslyn compile) off the action block itself via ObserveOn right
        // after the lock is acquired, so the delivery still returns Processed()
        // immediately and the action block is never blocked on a compile (the
        // leak that d936e31e2 fixed stays fixed).
        Execute(msg.Code, msg.Id, msg.Inputs, activityLogger, cts.Token)
            .Subscribe(
                returnValue =>
                {
                    ClearCancellationIf(cts);
                    // Capture the script's return value as JsonElement on the
                    // activity's terminal snapshot so handlers that triggered
                    // the script (e.g. ExportDocumentHandler) can deserialize
                    // it without a side-channel MeshNode.
                    JsonElement? returnElement = returnValue is null
                        ? null
                        : JsonSerializer.SerializeToElement(returnValue, hub.JsonSerializerOptions);
                    activityLogger.Complete(ActivityStatus.Succeeded, returnElement);
                    hub.Post(new SubmitCodeResponse(msg.Id, true), o => o.ResponseFor(request));
                },
                ex =>
                {
                    ClearCancellationIf(cts);
                    var canceled = ex is OperationCanceledException;
                    if (canceled)
                    {
                        activityLogger.LogWarning("Script execution cancelled by user");
                        activityLogger.Complete(ActivityStatus.Cancelled);
                    }
                    else
                    {
                        // Surface the full exception detail on the activity log
                        // (LogError on the standard formatter includes Type +
                        // Message + StackTrace via "{message}\n{exception}"),
                        // so subscribers see WHY the script failed, not just
                        // a generic "Failed" status.
                        activityLogger.LogError(ex, "Script execution failed: {Reason}", ex.Message);
                        activityLogger.Complete(ActivityStatus.Failed);
                    }
                    hub.Post(
                        new SubmitCodeResponse(msg.Id, false)
                        {
                            Error = canceled ? "Cancelled" : ex.Message
                        },
                        o => o.ResponseFor(request));
                });

        return request.Processed();
    }

    private void ClearCancellationIf(CancellationTokenSource cts)
    {
        lock (cancellationLock)
        {
            if (ReferenceEquals(activeCancellation, cts))
                activeCancellation = null;
        }
        cts.Dispose();
    }

    /// <summary>
    /// Run a single submission as an observable pipeline. Composition shape:
    /// <list type="number">
    ///   <item>Acquire <see cref="executionLock"/> (SDK boundary → <see cref="Observable.FromAsync(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task})"/>).</item>
    ///   <item>Resolve NuGet refs (SDK boundary → <see cref="Observable.FromAsync(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task})"/>).</item>
    ///   <item>Bind per-submission globals (sync — <c>Observable.Defer</c>).</item>
    ///   <item>Run Roslyn script under stdout capture (<c>Observable.Using</c> + Roslyn boundary).</item>
    ///   <item>Render return value if any (sync tail).</item>
    /// </list>
    /// Errors flow through <c>Observable.Catch</c> —
    /// <see cref="CompilationErrorException"/> wraps to <see cref="ScriptExecutionException"/>;
    /// other exceptions render an inline error control. The lock is always released
    /// via <see cref="Observable.Finally{TSource}"/>.
    /// </summary>
    private IObservable<object?> Execute(
        string code,
        string viewId,
        IReadOnlyDictionary<string, JsonElement> inputs,
        ILogger scriptOutputLogger,
        CancellationToken ct)
    {
        // executionLock is a serialization primitive (REPL ordering), not an I/O leaf —
        // so it bridges to the observable contract reactively (Defer keeps it cold so the
        // WaitAsync runs on Subscribe, inline on the action block, preserving FIFO acquire
        // order), NOT through Observable.FromAsync and NOT through an I/O pool.
        return Observable.Defer(() => executionLock.WaitAsync(ct).ToObservable())
            // The lock is acquired in submission order (the subscription runs
            // inline on the action block — see HandleSubmitCodeRequest). Hop to
            // the TaskPool ONLY for the work that follows, so the cold Roslyn
            // compile + AppDomain scan never run on the action block while still
            // preserving FIFO execution across submissions.
            .ObserveOn(TaskPoolScheduler.Default)
            .SelectMany(_ =>
            {
                EnsureInitialized();
                return nugetPool.Invoke(t => ResolveNuGetReferencesAsync(code, t))
                    .SelectMany(cleaned => RunOnePass(cleaned, viewId, scriptOutputLogger, inputs, ct));
            })
            .Catch<object?, Exception>(ex =>
            {
                if (ex is OperationCanceledException) return Observable.Throw<object?>(ex);
                if (ex is CompilationErrorException compEx)
                {
                    var msg = string.Join("\n", compEx.Diagnostics.Select(d => d.ToString()));
                    UpdateView(viewId, Controls.Markdown($"**Execution failed**:\n{msg}"));
                    return Observable.Throw<object?>(new ScriptExecutionException(msg, compEx));
                }
                UpdateView(viewId, Controls.Markdown($"**Execution failed**:\n{ex.Message}"));
                return Observable.Throw<object?>(ex);
            })
            .Finally(() => executionLock.Release());
    }

    /// <summary>
    /// One Roslyn submission inside an active stdout-capture scope. Splits out
    /// from <see cref="Execute"/> so <c>Observable.Using</c>
    /// can scope the <see cref="LoggerTextWriter"/> + <see cref="CapturingTextWriter"/>
    /// pair to the lifetime of the script run. Emits the script's
    /// <c>ReturnValue</c> (possibly null) so the caller can publish it on the
    /// activity log's terminal snapshot.
    /// </summary>
    private IObservable<object?> RunOnePass(
        string cleaned,
        string viewId,
        ILogger scriptOutputLogger,
        IReadOnlyDictionary<string, JsonElement> inputs,
        CancellationToken ct)
    {
        scriptLogger!.Set(scriptOutputLogger);
        scriptGlobals!.Ct = ct;
        scriptGlobals.Inputs = inputs ?? ImmutableDictionary<string, JsonElement>.Empty;

        return Observable.Using(
            () =>
            {
                var stdoutPipe = new LoggerTextWriter(scriptOutputLogger);
                var capture = CapturingTextWriter.Capture(stdoutPipe);
                return new StdoutScope(stdoutPipe, capture);
            },
            // Roslyn compile+execute on the bounded Compile IoPool (see `compilePool`) —
            // NOT a bare Observable.FromAsync on the shared ThreadPool. `t` is the pool's
            // cancellation (cancelled on unsubscribe), identical to the prior FromAsync CT,
            // so REPL state + CancelScriptRequest semantics are preserved.
            scope => compilePool.Invoke(t => scriptState is null
                    ? CSharpScript.RunAsync(cleaned, scriptOptions, scriptGlobals, typeof(MeshScriptGlobals), t)
                    : scriptState.ContinueWithAsync(cleaned, scriptOptions, t))
                .Select(state =>
                {
                    scriptState = state;
                    scope.StdoutPipe.Flush();
                    if (state.ReturnValue is not null)
                    {
                        UpdateView(viewId, state.ReturnValue);
                        scriptOutputLogger.LogInformation("{Value}", state.ReturnValue.ToString() ?? "");
                    }
                    return state.ReturnValue;
                }));
    }

    private sealed class StdoutScope(LoggerTextWriter stdoutPipe, IDisposable capture) : IDisposable
    {
        public LoggerTextWriter StdoutPipe { get; } = stdoutPipe;
        public void Dispose()
        {
            capture.Dispose();
            StdoutPipe.Dispose();
        }
    }

    /// <summary>
    /// Build script options once on first submission. Pulls the IMessageHub for
    /// the globals from the public hub (the parent), so scripts see the SAME hub
    /// external callers do — they can post messages, subscribe streams, etc.
    /// </summary>
    private void EnsureInitialized()
    {
        if (initialized) return;
        initialized = true;

        var defaultLogger = publicHub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.Kernel.Script");
        scriptLogger = new ScriptLogger(defaultLogger);
        scriptGlobals = new MeshScriptGlobals { Mesh = publicHub, Log = scriptLogger };

        var refs = new HashSet<Assembly>
        {
            typeof(IMessageHub).Assembly,
            typeof(Address).Assembly,
            typeof(UiControl).Assembly,
            typeof(DataExtensions).Assembly,
            typeof(EntityStore).Assembly,
            typeof(System.ComponentModel.DescriptionAttribute).Assembly,
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute).Assembly,
            typeof(System.Reactive.Linq.Observable).Assembly,
            typeof(FluentIcons).Assembly,
            typeof(ILogger).Assembly,
            typeof(LoggerExtensions).Assembly,
            typeof(MeshScriptGlobals).Assembly,
        };

        // Pull in every loaded non-dynamic assembly with a usable Location so scripts
        // can reach types from packages the host already loaded (Markdig, Newtonsoft,
        // Microsoft.Extensions.*, etc.). Mirrors ScriptCompilationService's TPA scan.
        //
        // Filter out assemblies whose Location file no longer exists on disk —
        // collectible NodeType ALCs leave Assembly objects in AppDomain after a
        // test deletes their cache directory; Roslyn would call
        // MetadataReference.CreateFromFile(Location) and throw "Could not find a
        // part of the path", which silently drops the ENTIRE references set
        // (so even MeshWeaver.Layout becomes invisible — see kernel test cluster
        // failure on shared-process CI runs).
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            if (string.IsNullOrEmpty(asm.Location)) continue;
            if (!File.Exists(asm.Location)) continue;
            refs.Add(asm);
        }

        // Modules that ship script templates (export, import, …) register their
        // own assembly via DI so it's guaranteed in the references set even if it
        // hasn't been touched yet at AppDomain scan time. Each module pushes one
        // <see cref="KernelScriptAssembly"/> singleton (or singletons) and we
        // enumerate them here.
        foreach (var contrib in publicHub.ServiceProvider
                     .GetServices<KernelScriptAssembly>())
        {
            refs.Add(contrib.Assembly);
        }

        scriptOptions = ScriptOptions.Default
            .WithReferences(refs)
            .WithImports(
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "System.ComponentModel",
                "System.ComponentModel.DataAnnotations",
                "System.Reactive.Linq",
                "System.Text.Json",
                "Microsoft.Extensions.Logging",
                "MeshWeaver.Application.Styles",
                "MeshWeaver.Layout",
                "MeshWeaver.Layout.DataGrid",
                "MeshWeaver.Messaging",
                "MeshWeaver.Mesh"
            )
            .WithEmitDebugInformation(true);
    }

    /// <summary>
    /// Per-hub assembly probing resolver — NO static state (NoStaticState.md). The
    /// probing-dir set and the <see cref="AssemblyLoadContext.Default"/> Resolving
    /// hook live and die with the kernel hub: the hook is removed on hub disposal,
    /// so package dirs never bleed across meshes/tests.
    /// </summary>
    internal sealed class AssemblyProbingResolver : IDisposable
    {
        private readonly HashSet<string> probingDirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new();
        private readonly Func<AssemblyLoadContext, System.Reflection.AssemblyName, System.Reflection.Assembly?> handler;

        public AssemblyProbingResolver()
        {
            handler = (ctx, name) =>
            {
                var dllName = name.Name + ".dll";
                lock (gate)
                {
                    foreach (var d in probingDirs)
                    {
                        var candidate = Path.Combine(d, dllName);
                        if (File.Exists(candidate))
                        {
                            try { return ctx.LoadFromAssemblyPath(candidate); }
                            catch { /* try next */ }
                        }
                    }
                }
                return null;
            };
            AssemblyLoadContext.Default.Resolving += handler;
        }

        public void AddDirectories(IEnumerable<string> dirs)
        {
            lock (gate)
                foreach (var dir in dirs)
                    probingDirs.Add(dir);
        }

        public void Dispose() => AssemblyLoadContext.Default.Resolving -= handler;
    }

    private void InstallRuntimeProbe(IEnumerable<string> dirs)
    {
        // Get-or-create on the hub's property bag. A concurrent first-install can
        // create two resolvers — benign: both consult their own dir sets, both
        // unhook at hub disposal; no wrong assembly can resolve.
        var resolver = publicHub.Get<AssemblyProbingResolver>();
        if (resolver is null)
        {
            resolver = new AssemblyProbingResolver();
            publicHub.Set(resolver);
            publicHub.RegisterForDisposal(resolver);
        }
        resolver.AddDirectories(dirs);
    }

    private async Task<string> ResolveNuGetReferencesAsync(string source, CancellationToken ct)
    {
        var (cleaned, refs) = NuGetDirectiveParser.Extract(source);
        if (refs.Length == 0) return source;

        var resolver = publicHub.ServiceProvider.GetService<INuGetAssemblyResolver>();
        if (resolver is null)
        {
            Logger.LogWarning("INuGetAssemblyResolver not registered; #r \"nuget:...\" directives ignored.");
            return cleaned;
        }

        try
        {
            var resolved = await resolver.ResolveAsync(refs, targetFramework: null, ct);
            scriptOptions = scriptOptions.AddReferences(
                resolved.AssemblyPaths.Select(p => MetadataReference.CreateFromFile(p)));
            InstallRuntimeProbe(resolved.ProbingDirectories);
            Logger.LogInformation("Resolved {Count} NuGet package(s) for interactive cell.", refs.Length);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "NuGet restore failed for interactive cell.");
            throw;
        }

        return cleaned;
    }

    /// <summary>
    /// Update the layout area on the PUBLIC hub (parent), not the executor.
    /// Layout subscribers are attached to the parent's address — that's where
    /// the area stream lives.
    /// </summary>
    private void UpdateView(string viewId, object view)
    {
        var areasStream = publicHub.ServiceProvider.GetRequiredService<ISynchronizationStream<ImmutableDictionary<string, object>>>();
        areasStream.Update(x =>
            new ChangeItem<ImmutableDictionary<string, object>>(
                (x ?? ImmutableDictionary<string, object>.Empty).SetItem(viewId, view),
                publicHub.Address,
                areasStream.StreamId,
                ChangeType.Patch,
                publicHub.Version,
                [])
        , _ => { });
    }
}

/// <summary>
/// Wraps a Roslyn-script compile/runtime failure with the diagnostic message
/// surfaced to the caller. Distinct type so callers can pattern-match.
/// </summary>
public sealed class ScriptExecutionException(string message, Exception inner) : Exception(message, inner);
