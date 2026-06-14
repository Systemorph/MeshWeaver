using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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

    // REPL submissions run STRICTLY in arrival order on a 100%-reactive serial queue:
    // Concat subscribes the next submission only AFTER the previous Execute completes
    // (i.e. after scriptState is assigned), so block #2 always sees block #1's variables.
    // This replaces a hand-woven SemaphoreSlim — per ControlledIoPooling.md the ONLY
    // concurrency primitives are the IoPools. The compile LEAF inside Execute runs on the
    // shared Compile pool (which bounds compiles ACROSS kernels); ordering WITHIN a kernel
    // is this Concat, not a lock. Push order == the executor action block's FIFO handling.
    private readonly Subject<Submission> submissions = new();
    private IDisposable? submissionPump;

    /// <summary>One queued REPL submission + the sink that carries its outcome back to the
    /// posting handler (so the serial Concat pump stays decoupled from request/response).</summary>
    private sealed record Submission(
        string Code,
        string ViewId,
        IReadOnlyDictionary<string, JsonElement> Inputs,
        ILogger ScriptOutputLogger,
        CancellationToken Ct,
        AsyncSubject<object?> Result);

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
        // Start the serial REPL processor ONCE. Concat runs queued submissions strictly in
        // arrival order — each Execute fully completes (scriptState assigned) before the next
        // subscribes — so cross-submission state sharing is deterministic without any lock.
        submissionPump = submissions.Select(RunSubmission).Concat().Subscribe();
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
    /// Sync entry — correlates this submission's outcome (carried on an
    /// <see cref="AsyncSubject{T}"/>) back to the request/response, then ENQUEUES it on
    /// the serial Concat pump (see <see cref="submissions"/>). The pump runs submissions
    /// strictly in arrival order and never blocks the action block — ordering across
    /// submissions comes from Concat, NOT a lock; the action block returns
    /// <see cref="IMessageDelivery"/> immediately.
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

        // The submission's outcome arrives on this AsyncSubject (one value + completion on
        // success, OnError on failure/cancel). Wire the request/response off it, then push
        // the submission onto the serial pump — it executes once all earlier submissions
        // have completed, guaranteeing block #2 sees block #1's ScriptState.
        var result = new AsyncSubject<object?>();
        result.Subscribe(
            returnValue =>
            {
                ClearCancellationIf(cts);
                // Capture the script's return value as JsonElement on the activity's
                // terminal snapshot so handlers that triggered the script (e.g.
                // ExportDocumentHandler) can deserialize it without a side-channel MeshNode.
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
                    // Surface the full exception detail on the activity log (LogError on the
                    // standard formatter includes Type + Message + StackTrace), so subscribers
                    // see WHY the script failed, not just a generic "Failed" status.
                    activityLogger.LogError(ex, "Script execution failed: {Reason}", ex.Message);
                    activityLogger.Complete(ActivityStatus.Failed);
                }
                hub.Post(
                    new SubmitCodeResponse(msg.Id, false) { Error = canceled ? "Cancelled" : ex.Message },
                    o => o.ResponseFor(request));
            });

        submissions.OnNext(new Submission(msg.Code, msg.Id, msg.Inputs, activityLogger, cts.Token, result));
        return request.Processed();
    }

    /// <summary>
    /// One inner of the serial Concat pump: runs a submission's <see cref="Execute"/>,
    /// forwards its outcome to the submission's <see cref="AsyncSubject{T}"/> result sink,
    /// and ALWAYS completes its own (Unit) signal — even on failure — so a failed submission
    /// never tears down the pump and Concat advances to the next one.
    /// </summary>
    private IObservable<Unit> RunSubmission(Submission s) =>
        Observable.Create<Unit>(downstream =>
            Execute(s.Code, s.ViewId, s.Inputs, s.ScriptOutputLogger, s.Ct)
                .Subscribe(
                    value => s.Result.OnNext(value),
                    ex =>
                    {
                        s.Result.OnError(ex);
                        downstream.OnNext(Unit.Default);
                        downstream.OnCompleted();
                    },
                    () =>
                    {
                        s.Result.OnCompleted();
                        downstream.OnNext(Unit.Default);
                        downstream.OnCompleted();
                    }));

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
    ///   <item>Resolve NuGet refs (Http IoPool leaf).</item>
    ///   <item>Bind per-submission globals + run Roslyn script under stdout capture
    ///         (<c>Observable.Using</c> + the Compile IoPool leaf in RunOnePass).</item>
    ///   <item>Render return value if any (sync tail).</item>
    /// </list>
    /// Ordering across submissions is the serial Concat pump's job (no lock here).
    /// Errors flow through <c>Observable.Catch</c> —
    /// <see cref="CompilationErrorException"/> wraps to <see cref="ScriptExecutionException"/>;
    /// other exceptions render an inline error control.
    /// </summary>
    private IObservable<object?> Execute(
        string code,
        string viewId,
        IReadOnlyDictionary<string, JsonElement> inputs,
        ILogger scriptOutputLogger,
        CancellationToken ct)
    {
        // No lock and no gate here: REPL ordering across submissions is owned by the serial
        // Concat pump (see `submissions`), which subscribes this observable only after the
        // previous submission completed. So Execute is just: init → resolve NuGet refs → run
        // the Roslyn pass. SubscribeOn moves the WHOLE subscribe — including
        // EnsureInitialized's AppDomain scan — onto the ThreadPool, so the executor action
        // block is never blocked by a compile. (The Roslyn compile itself runs on the bounded
        // Compile IoPool inside RunOnePass; NuGet restore on the Http pool.)
        return Observable.Defer(() =>
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
            .SubscribeOn(TaskPoolScheduler.Default);
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

        // Curated anchors + DI-contributed module assemblies. The full reference
        // set ("every loaded non-dynamic assembly with a usable Location, so
        // scripts can reach types from packages the host already loaded") comes
        // from KernelScriptReferences — a process-shared, materialized-ONCE
        // PortableExecutableReference snapshot. 🚨 Never pass raw Assembly objects
        // to WithReferences here: Roslyn then materializes a fresh AssemblyMetadata
        // + native metadata block per reference PER SESSION (~350 refs ≈ 150-200 MiB
        // of native memory per kernel session, never reclaimed — the CI
        // memory-pressure leak; see KernelScriptReferences docs).
        var sessionAssemblies = new HashSet<Assembly>
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

        // Modules that ship script templates (export, import, …) register their
        // own assembly via DI so it's guaranteed in the references set even if it
        // hasn't been loaded yet when the shared snapshot was taken. Each module
        // pushes one <see cref="KernelScriptAssembly"/> singleton (or singletons)
        // and we enumerate them here.
        foreach (var contrib in publicHub.ServiceProvider
                     .GetServices<KernelScriptAssembly>())
        {
            sessionAssemblies.Add(contrib.Assembly);
        }

        scriptOptions = ScriptOptions.Default
            .WithReferences(KernelScriptReferences.GetReferences(sessionAssemblies))
            // 🚨 Required for the sharing to be complete: the compilation resolves
            // the globals type's transitive closure via ResolveMissingAssembly —
            // with the default resolver that re-materializes every assembly's
            // native metadata PER SESSION (the other half of the leak).
            .WithMetadataResolver(SharedScriptMetadataResolver.Instance)
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
                resolved.AssemblyPaths
                    .Select(KernelScriptReferences.GetOrCreateFromFile)
                    .Where(r => r is not null)
                    .Select(r => (MetadataReference)r!));
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
