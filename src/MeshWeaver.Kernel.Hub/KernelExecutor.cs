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
using MeshWeaver.Compiler;
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
    // The REPL chain + its collectible AssemblyLoadContext — see ScriptSession. Owning the
    // emit/load path (instead of ScriptState.RunAsync) is what lets the per-cell assemblies
    // unload with the session; Roslyn's own script loader is permanently non-collectible.
    private ScriptSession? session;
    private ScriptOptions scriptOptions = ScriptOptions.Default;
    private MeshScriptGlobals? scriptGlobals;
    private ScriptLogger? scriptLogger;
    private bool initialized;

    // The cell-surface pack bind (issue #1649): name → Assembly for exactly the DynamicNode_*
    // assemblies THIS session's reference set declared (see EnsureCellSurfaceReferences). The
    // session's load context resolves these names here instead of falling through to the
    // Default ALC, which cannot see collectible node contexts. Null until the first successful
    // resolution — a failed resolution is NOT latched, so the next submission re-attempts.
    // 🚨 These strong references + the leases below deliberately PIN the referenced collectible
    // generation until this executor (== the kernel session) dies; a NodeType recompile
    // mid-session keeps this session on the old generation, new sessions bind the new one.
    private ImmutableDictionary<string, Assembly>? cellSurfaceBindings;
    private readonly System.Reactive.Disposables.CompositeDisposable cellSurfaceLeases = new();

    // The session's module surface (ModuleScriptBindings): the module generations current when the
    // reference set was built — compiled against, and bound at run time (entry assembly or private
    // dependency, through the module's own context, leased until the session ends). Set with
    // scriptOptions in EnsureInitializedAsync, disposed with the session.
    private ModuleScriptBindings? moduleBindings;

    // REPL submissions run STRICTLY in arrival order on a 100%-reactive serial queue:
    // Concat subscribes the next submission only AFTER the previous Execute completes
    // (i.e. after scriptState is assigned), so block #2 always sees block #1's variables.
    // This replaces a hand-woven SemaphoreSlim — per ControlledIoPooling.md the ONLY
    // concurrency primitives are the IoPools. The compile LEAF inside Execute runs on the
    // shared CPU lane (which bounds compiles ACROSS kernels); ordering WITHIN a kernel
    // is this Concat, not a lock. Push order == the executor action block's FIFO handling.
    private readonly Subject<Submission> submissions = new();

    // Per-SUBMISSION settlement signals, keyed by the forwarded delivery's id (KernelContainer sets
    // it). The container's idle-disconnect claim waits on one when its bounded wait for the reply
    // times out — the claim must last until THIS submission ends, and an Activity's status cannot
    // say that: one Activity carries many submissions. AsyncSubject, so a settlement that lands
    // before the container subscribes is still seen.
    private ImmutableDictionary<string, AsyncSubject<Unit>> settlements =
        ImmutableDictionary<string, AsyncSubject<Unit>>.Empty;

    /// <summary>The settlement signal of the submission delivered under <paramref name="deliveryId"/>:
    /// emits once and completes when the executor has settled it (any outcome) or is torn down.</summary>
    internal IObservable<Unit> ExpectSettlement(string deliveryId)
        => ImmutableInterlocked.GetOrAdd(ref settlements, deliveryId, _ => new AsyncSubject<Unit>());

    /// <summary>Drops an expected settlement whose submission never reached this executor.</summary>
    internal void ForgetSettlement(string deliveryId)
        => ImmutableInterlocked.TryRemove(ref settlements, deliveryId, out _);

    private void Settle(string deliveryId)
    {
        if (!ImmutableInterlocked.TryRemove(ref settlements, deliveryId, out var settled))
            return;
        settled.OnNext(Unit.Default);
        settled.OnCompleted();
    }
    private IDisposable? submissionPump;

    /// <summary>One queued REPL submission + the sink that carries its outcome back to the
    /// posting handler (so the serial Concat pump stays decoupled from request/response).
    /// <see cref="AccessContext"/> is the DELIVERY's identity, captured at the handler — the
    /// script is explicitly re-stamped with it before it runs (see <see cref="RunOnePass"/>),
    /// so its effective identity never depends on whatever AsyncLocal happens to ride the
    /// reactive chain to the compile pool (a System-scoped resolution step upstream — e.g. the
    /// cell-surface provider's discovery query, whose response emission the framework
    /// deliberately re-stamps with the OBSERVING caller's context — would otherwise leak
    /// System into the script and render content the submitting user may not read).</summary>
    private sealed record Submission(
        string Code,
        string ViewId,
        IReadOnlyDictionary<string, JsonElement> Inputs,
        ILogger ScriptOutputLogger,
        CancellationToken Ct,
        AsyncSubject<object?> Result,
        AccessContext? AccessContext);

    // 🚦 A cell's EXECUTION (user code that awaits) and the session warm-up WAIT run on their
    // own lane — IoPoolNames.KernelScript — never a bare Observable.FromAsync on the shared
    // ThreadPool, and never the Compile pool. They used to be async leaves of Compile, and that
    // was the nested-gate deadlock: a running script can wait on another of that pool's async
    // leaves — ANOTHER kernel session's warm-up or execution (a cell that runs a cell), a
    // language-service compile. With every Compile slot held by a script waiting on such a leaf,
    // none is ever admitted, no script finishes, and every Activity involved stays Running for
    // ever with nothing logged (KernelScriptHoldsNoCompileSlotTest pins it). The CPU half of a
    // cell (build, emit, load) runs on the CPU lane below, which cannot nest by construction.
    // Unbounded fallback only when no registry is wired (DI-less); still offloads, no cap.
    private readonly IIoPool scriptLane =
        publicHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.KernelScript)
            ?? IoPool.Unbounded;

    // 🚨 REFUSED, never QUEUED. Moving off Compile alone only moved the cycle: a cell that runs a
    // cell holds its script-lane slot while it waits, and the child needs a slot of the SAME lane to
    // warm up and execute — so with every slot held by a waiting parent, a queued child is never
    // admitted and the whole tree stays Running. Admission without a queue turns that state into a
    // terminal failure of the child's run (its Activity reads Failed with the refusal), which its
    // parent then sees. Unbounded fallback (no registry) has no slots to exhaust.
    private IObservable<T> AdmitToScriptLane<T>(Func<CancellationToken, Task<T>> io)
        => scriptLane is IoPool bounded ? bounded.InvokeOrRefuse(io) : scriptLane.Invoke(io);

    // The CPU lane: a cell's Roslyn build + emit + load runs here, on the lane's own threads, never
    // a ThreadPool worker (#5388, Doc/Architecture/CompileOffTheThreadPool). A leaf on it does
    // nothing but compute — ScriptSession.Compile — so it cannot nest under the Compile pool's gate.
    private readonly IIoPool cpuLane =
        publicHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.CompileCpu)
            ?? IoPool.Unbounded;

    // Every submission this executor has accepted and not yet settled — running AND queued —
    // keyed by its delivery id, with its outcome sink and its own cancellation (#7362, option A).
    // Two things need the WHOLE set, never "the one currently running":
    //   * a cancel (CancelScriptRequest — the Activity's RequestedStatus = Cancelled) cancels every
    //     run of this Activity, wherever it is parked: queued behind the Concat pump, in a
    //     preparation stage, or in the script itself. It used to cancel only the LAST submission
    //     handed in, which is a queued one as soon as two are pending, and the token reached only
    //     the script pass — a run parked in a preparation stage could not be cancelled at all.
    //   * the executor's disposal settles every one of them Failed, with a stated reason. Disposal
    //     used to stop the pump and release the settlement waiters but write NOTHING, so every run
    //     in flight or queued stayed Running for ever.
    // Immutable map + ImmutableInterlocked: no lock. The CancellationTokenSources are never
    // disposed — they own no timer and no linked token, and a source disposed while the cancel or
    // the disposal path is cancelling it would throw there.
    private ImmutableDictionary<string, PendingRun> pendingRuns =
        ImmutableDictionary<string, PendingRun>.Empty;

    private sealed record PendingRun(AsyncSubject<object?> Result, CancellationTokenSource Cancellation);

    private ILogger Logger => publicHub.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<KernelExecutor>();

    public MessageHubConfiguration Configure(MessageHubConfiguration config)
    {
        config.TypeRegistry.WithType(typeof(SubmitCodeRequest), nameof(SubmitCodeRequest));
        config.TypeRegistry.WithType(typeof(SubmitCodeResponse), nameof(SubmitCodeResponse));
        config.TypeRegistry.WithType(typeof(CancelScriptRequest), nameof(CancelScriptRequest));
        // Start the serial REPL processor ONCE. Concat runs queued submissions strictly in
        // arrival order — each Execute fully completes (the session's chain advanced) before
        // the next subscribes — so cross-submission state sharing is deterministic without any
        // lock.
        // RunSubmission settles every fault into a completion, so the pump is not expected to
        // fault — but if it ever does, the fault is owned here: a no-argument Subscribe() hands
        // it to Rx's default onError, which rethrows on the delivering thread (MeshWeaver#5650).
        submissionPump = submissions.Select(RunSubmission).Concat().Subscribe(
            _ => { },
            ex => Logger.LogError(ex,
                "Kernel submission pump terminated with a fault; no further submission will run on this session"));
        return config
            .WithInitialization(hub =>
            {
                // 🚨 #7362 — every run this executor still owes an answer is failed, with its
                // reason, at the FIRST instant of teardown: this hub's own disposal, or its host's
                // (the Activity hub's freeze cascades here synchronously inside its Dispose()).
                // Not in the disposal registrants below: those run in the ShutDown phase, AFTER
                // the host has quiesced, force-cancelled the forwarded submission and stopped
                // taking writes — a terminal write issued there never reaches the Activity, and
                // the run stays Running (or is failed by the dispatcher with no reason of the
                // kernel's). ShuttingDown replays, so subscribing late still fires.
                hub.RegisterForDisposal(hub.ShuttingDown.Subscribe(
                    _ => FailPendingRuns(),
                    ex => Logger.LogWarning(ex, "Kernel executor {Address}: shutdown signal faulted", hub.Address)));
                // Executor hub disposal (idle disconnect / session close) eagerly unloads the
                // session's collectible AssemblyLoadContext — the per-cell script assemblies
                // die with the session instead of accumulating for the process lifetime — and
                // stops the pump. Disposing the session while a script is still running is
                // safe: unloading is cooperative and completes when the last reference dies.
                hub.RegisterForDisposal(System.Reactive.Disposables.Disposable.Create(() =>
                {
                    // A run that reached the executor after the shutdown signal is settled here
                    // (a no-op when the signal already settled everything); then stop the pump.
                    FailPendingRuns();
                    submissionPump?.Dispose();
                    // Nothing here will settle any more: release every waiter (the claim's dead-executor
                    // check would too, but only at the next idle tick).
                    foreach (var deliveryId in settlements.Keys)
                        Settle(deliveryId);
                    session?.Dispose();
                    session = null;
                    // Release the cell-surface generation pins with the session — after this,
                    // a superseded NodeType generation this session was bound to can unload.
                    cellSurfaceLeases.Dispose();
                    // …and the module generations it bound into, likewise.
                    moduleBindings?.Dispose();
                }));
            })
            .WithHandler<SubmitCodeRequest>(HandleSubmitCodeRequest)
            .WithHandler<CancelScriptRequest>(HandleCancelRequest);
    }

    private IMessageDelivery HandleCancelRequest(IMessageHub _, IMessageDelivery<CancelScriptRequest> request)
    {
        // Every run of this Activity — queued, preparing or executing. Each run's token is
        // observed by every stage of its pipeline (see Execute), so wherever it is parked it ends
        // Cancelled.
        foreach (var run in pendingRuns.Values)
            run.Cancellation.Cancel();
        return request.Processed();
    }

    /// <summary>
    /// The executor is going away: every run it accepted and has not settled is failed with a
    /// keyed, localized reason, its caller answered and its settlement released (#7362). The
    /// outcome sink is the same one a script failure goes through, so the Activity's terminal
    /// write, the <see cref="SubmitCodeResponse"/> and the settlement happen exactly as they do for
    /// any other failure — and <c>ActivityLogLogger.Complete</c> is idempotent, so a run that
    /// settled a moment earlier keeps its own verdict. Only AFTER the verdict is recorded is the
    /// run's token cancelled, so a script still executing on the lane is asked to stop: cancelling
    /// first would run the token's registrations synchronously and record the run as Cancelled —
    /// a user's act it never was.
    /// </summary>
    private void FailPendingRuns()
    {
        foreach (var deliveryId in pendingRuns.Keys)
        {
            if (!ImmutableInterlocked.TryRemove(ref pendingRuns, deliveryId, out var run))
                continue;
            try
            {
                run.Result.OnError(new KernelHostShutDownException());
            }
            catch (Exception sinkEx)
            {
                Logger.LogError(sinkEx,
                    "Submission {DeliveryId}: outcome sink threw while failing the run on kernel shutdown",
                    deliveryId);
            }
            finally
            {
                run.Cancellation.Cancel();
            }
        }
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
        // 🚨 publicHub, NOT this executor hub. The executor is a lightweight kernel sub-hub with
        // no mesh data source, so it has no workspace to write the activity node through; the
        // public (Activity) hub OWNS that node, which makes the logger's writes plain own-node
        // writes — minted version, serialised by the owner's action block, no mirror (#1784).
        var activityLogger = new ActivityLogLogger(publicHub, activityPath);

        // Pair each submission with its own CancellationTokenSource; it is registered as a pending
        // run below, so a cancel and the executor's disposal reach it wherever it is parked.
        var cts = new CancellationTokenSource();

        // The submission's outcome arrives on this AsyncSubject (one value + completion on
        // success, OnError on failure/cancel). Wire the request/response off it, then push
        // the submission onto the serial pump — it executes once all earlier submissions
        // have completed, guaranteeing block #2 sees block #1's ScriptState.
        //
        // The settle path (activity terminal write + response post) is re-stamped with the
        // DELIVERY's identity: it runs on whatever thread the pipeline terminated on, whose
        // ambient AsyncLocal reflects the pipeline's history, not this submission's caller.
        // Hub-shaped principals are excluded exactly as MessageHub's own handler stamping
        // excludes them — a hub-stamped post must not become the script's principal.
        var submissionContext =
            request.AccessContext is { } deliveryContext
            && !AccessService.LooksLikeHubPrincipal(deliveryContext.ObjectId)
                ? deliveryContext
                : null;
        var accessService = publicHub.ServiceProvider.GetService<AccessService>();
        IDisposable? RestoreSubmissionContext() =>
            submissionContext is null ? null : accessService?.SwitchAccessContext(submissionContext);
        var result = new AsyncSubject<object?>();
        result.Subscribe(
            returnValue =>
            {
                using var _ = RestoreSubmissionContext();
                ForgetRun(request.Id);
                try
                {
                    // Capture the script's return value as JsonElement on the activity's
                    // terminal snapshot so handlers that triggered the script (e.g.
                    // ExportDocumentHandler) can deserialize it without a side-channel MeshNode.
                    JsonElement? returnElement = returnValue is null
                        ? null
                        : JsonSerializer.SerializeToElement(returnValue, hub.JsonSerializerOptions);
                    activityLogger.Complete(ActivityStatus.Succeeded, returnElement);
                    hub.Post(new SubmitCodeResponse(msg.Id, true) { ActivitySettled = true },
                        o => o.ResponseFor(request));
                    Settle(request.Id);
                }
                catch (Exception settleEx)
                {
                    // 🚨 The settle path itself faulted — canonically SerializeToElement on a
                    // non-serializable script return value (throwing getter, unsupported type).
                    // Wedges-to-zero: route the fault to the activity's graceful sink and STILL
                    // settle terminally. Before this guard the exception skipped Complete
                    // entirely (activity stuck Running forever — the zombie-activity shape) and
                    // unwound through the AsyncSubject into the Concat pump, wedging every later
                    // submission on this kernel. Complete is idempotent, so the Failed settle
                    // here can never overwrite an already-published Succeeded.
                    activityLogger.LogError(settleEx,
                        "Failed to record script result: {Reason}", settleEx.Message);
                    activityLogger.Complete(ActivityStatus.Failed);
                    hub.Post(
                        new SubmitCodeResponse(msg.Id, false) { Error = settleEx.Message, ActivitySettled = true },
                        o => o.ResponseFor(request));
                    Settle(request.Id);
                }
            },
            ex =>
            {
                using var _ = RestoreSubmissionContext();
                ForgetRun(request.Id);
                var canceled = ex is OperationCanceledException;
                if (canceled)
                {
                    activityLogger.LogWarning("Script execution cancelled by user");
                    activityLogger.Complete(ActivityStatus.Cancelled);
                }
                else if (ex is KernelHostShutDownException)
                {
                    // Platform-owned, so KEYED: the Output pane renders it in the viewer's language.
                    activityLogger.Append(new LogMessage(
                            "The kernel host shut down while this run was in progress, so the run did not "
                            + "finish. Run the cell again.",
                            LogLevel.Error)
                        .WithKey(KernelHostShutDownException.MessageKey));
                    activityLogger.Complete(ActivityStatus.Failed);
                }
                else if (ex is IoPoolSaturatedException saturated)
                {
                    // A platform-owned refusal, not script output: it is recorded KEYED so the
                    // Output pane renders it in the viewer's language; the exception text stays for
                    // the host's diagnostics only.
                    Logger.LogWarning(ex, "Submission {ViewId} refused: the kernel script lane is full", msg.Id);
                    activityLogger.Append(new LogMessage(
                            $"The cell was not run: all {saturated.MaxConcurrency} kernel slot(s) of this "
                            + "process are busy, and a cell that waits on another cell may not queue behind it. "
                            + "Run it again once other cells have finished.",
                            LogLevel.Error)
                        .WithKey("activity.codeRun.kernelSaturated", ("max", saturated.MaxConcurrency)));
                    activityLogger.Complete(ActivityStatus.Failed);
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
                    new SubmitCodeResponse(msg.Id, false)
                    {
                        Error = canceled ? "Cancelled" : ex.Message,
                        ActivitySettled = true,
                    },
                    o => o.ResponseFor(request));
                Settle(request.Id);
            });

        ImmutableInterlocked.TryAdd(ref pendingRuns, request.Id, new PendingRun(result, cts));
        submissions.OnNext(new Submission(
            msg.Code, msg.Id, msg.Inputs, activityLogger, cts.Token, result, submissionContext));
        return request.Processed();
    }

    /// <summary>
    /// One inner of the serial Concat pump: runs a submission's <see cref="Execute"/>,
    /// forwards its outcome to the submission's <see cref="AsyncSubject{T}"/> result sink,
    /// and ALWAYS completes its own (Unit) signal — even on failure — so a failed submission
    /// never tears down the pump and Concat advances to the next one.
    /// <para>🚨 The OnError/OnCompleted calls on the result sink run the settle path
    /// (activity terminal write + response post) SYNCHRONOUSLY. A sink that throws must
    /// never wedge the pump: the settle path self-handles its faults (see
    /// <see cref="HandleSubmitCodeRequest"/>), and anything still escaping is logged here
    /// while the <c>finally</c> guarantees the pump advances — before this, a sink throw
    /// skipped the downstream signal and every later submission queued forever.</para>
    /// </summary>
    private IObservable<Unit> RunSubmission(Submission s) =>
        Observable.Create<Unit>(downstream =>
            Execute(s.Code, s.ViewId, s.Inputs, s.ScriptOutputLogger, s.AccessContext, s.Ct)
                .Subscribe(
                    value => s.Result.OnNext(value),
                    ex =>
                    {
                        try
                        {
                            s.Result.OnError(ex);
                        }
                        catch (Exception sinkEx)
                        {
                            Logger.LogError(sinkEx,
                                "Submission {ViewId}: outcome sink threw while settling failure",
                                s.ViewId);
                        }
                        finally
                        {
                            downstream.OnNext(Unit.Default);
                            downstream.OnCompleted();
                        }
                    },
                    () =>
                    {
                        try
                        {
                            s.Result.OnCompleted();
                        }
                        catch (Exception sinkEx)
                        {
                            Logger.LogError(sinkEx,
                                "Submission {ViewId}: outcome sink threw while settling success",
                                s.ViewId);
                        }
                        finally
                        {
                            downstream.OnNext(Unit.Default);
                            downstream.OnCompleted();
                        }
                    }));

    private void ForgetRun(string deliveryId)
        => ImmutableInterlocked.TryRemove(ref pendingRuns, deliveryId, out _);

    /// <summary>
    /// Run a single submission as an observable pipeline. Composition shape:
    /// <list type="number">
    ///   <item>Resolve NuGet refs (Http IoPool leaf).</item>
    ///   <item>Bind per-submission globals + run Roslyn script under stdout capture
    ///         (<c>Observable.Using</c> + the CPU-lane compile and script-lane execute leaves in RunOnePass).</item>
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
        AccessContext? accessContext,
        CancellationToken ct)
    {
        // No lock and no gate here: REPL ordering across submissions is owned by the serial
        // Concat pump (see `submissions`), which subscribes this observable only after the
        // previous submission completed. So Execute is just: init → resolve NuGet refs → run
        // the Roslyn pass. SubscribeOn moves the WHOLE subscribe onto the ThreadPool, so the
        // executor action block is never blocked by a compile. EnsureInitializedAsync's
        // AppDomain scan is ADDITIONALLY a pooled leaf (issues #2480/#2578) — it used to run as
        // a bare synchronous call here, off any pool, which meant it could neither be tracked
        // nor cancelled by silo/mesh teardown. Its pool is the script lane, not Compile: the
        // leaf is mostly a WAIT on the process-wide warm-up, and a Compile slot held across a
        // wait starves every other async Compile leaf (other sessions, the language service).
        //
        // 🚨 The run's token governs EVERY stage, not only the script pass (#7362). The preparation
        // stages are waits on shared work — the process-wide warm-up, the cell-surface resolution,
        // a NuGet restore — and none of them took the token, so a cancel of a run parked there did
        // nothing. They now run under UntilCancelled: a cancel (or a run cancelled while still
        // queued) ends the run Cancelled at once, and unsubscribing the stage cancels its pool
        // leaf. Each stage publishes its result into the session only after it completes, so an
        // abandoned stage leaves nothing half-set. The script pass keeps its own cooperative token:
        // abandoning a RUNNING script would let the next submission start beside it.
        return Observable.Defer(() =>
                UntilCancelled(
                        AdmitToScriptLane(t => EnsureInitializedAsync(t))
                            .SelectMany(_ => EnsureCellSurfaceReferences())
                            .SelectMany(_ => ResolveNuGetReferences(code)),
                        ct)
                    .SelectMany(cleaned => RunOnePass(cleaned, viewId, scriptOutputLogger, inputs, accessContext, ct)))
            .Catch<object?, Exception>(ex =>
            {
                if (ex is OperationCanceledException) return Observable.Throw<object?>(ex);
                // The result area is read by the person who RAN the cell, so it is rendered in the
                // language of the submission's own viewer — explicitly, never the ambient culture
                // (MeshWeaver#7363).
                UpdateView(viewId, FailureView(ex, accessContext?.Locale));
                return ex is CompilationErrorException compEx
                    ? Observable.Throw<object?>(new ScriptExecutionException(CompilerDiagnostics(compEx), compEx))
                    : Observable.Throw<object?>(ex);
            })
            .SubscribeOn(TaskPoolScheduler.Default);
    }

    /// <summary>
    /// <paramref name="source"/>, ended with an <see cref="OperationCanceledException"/> the moment
    /// <paramref name="ct"/> is cancelled (at once when it already is), unsubscribing the source.
    /// <c>Amb</c> forwards whichever side signals first and unsubscribes the other, so a value the
    /// source has already produced is never overtaken by a later cancel.
    /// </summary>
    internal static IObservable<T> UntilCancelled<T>(IObservable<T> source, CancellationToken ct)
        => source.Amb(Observable.Create<T>(observer =>
            ct.Register(() => observer.OnError(new OperationCanceledException(ct)))));

    /// <summary>The compiler's diagnostics, one per line — the reason a cell did not compile.</summary>
    private static string CompilerDiagnostics(CompilationErrorException compEx)
        => string.Join("\n", compEx.Diagnostics.Select(d => d.ToString()));

    /// <summary>
    /// What the cell's RESULT AREA shows when a submission fails, in <paramref name="locale"/>.
    /// <para>The heading is platform text and is keyed (<c>kernel.executionFailed</c>). The detail
    /// is the compiler's diagnostics or the script's own exception message — script output, which
    /// renders as written — except for a refusal by the kernel's script lane: that sentence is the
    /// platform's, so it renders through the same key the run's transcript uses
    /// (<c>activity.codeRun.kernelSaturated</c>) rather than the exception's English text.</para>
    /// </summary>
    /// <param name="ex">The failure.</param>
    /// <param name="locale">The language tag of the viewer who ran the cell; null renders English.</param>
    /// <returns>The control to render in the result area.</returns>
    internal static UiControl FailureView(Exception ex, string? locale)
    {
        var detail = ex switch
        {
            CompilationErrorException compEx => CompilerDiagnostics(compEx),
            IoPoolSaturatedException saturated => LocalizationCatalog.GetNamed(
                "activity.codeRun.kernelSaturated", locale,
                ImmutableDictionary<string, object>.Empty.Add("max", saturated.MaxConcurrency),
                ex.Message),
            _ => ex.Message,
        };
        return Controls.Markdown($"**{LocalizationCatalog.Get("kernel.executionFailed", locale)}**:\n{detail}");
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
        AccessContext? accessContext,
        CancellationToken ct)
    {
        scriptLogger!.Set(scriptOutputLogger);
        scriptGlobals!.Ct = ct;
        scriptGlobals.Inputs = inputs ?? ImmutableDictionary<string, JsonElement>.Empty;

        return Observable.Using(
            () =>
            {
                // Both standard streams are piped into the script's ActivityLog:
                // stdout as Information lines, stderr as Error lines — so
                // Console.Error prints surface on the run's output (red) instead
                // of vanishing into the host process's stderr.
                var stdoutPipe = new LoggerTextWriter(scriptOutputLogger);
                var stderrPipe = new LoggerTextWriter(scriptOutputLogger, LogLevel.Error);
                var capture = CapturingTextWriter.Capture(stdoutPipe, stderrPipe);
                return new StdoutScope(stdoutPipe, stderrPipe, capture);
            },
            // Roslyn compile on the CPU lane, execute on the script lane (see `scriptLane`) —
            // NOT a bare Observable.FromAsync on the shared ThreadPool. `t` is the pool's
            // cancellation (cancelled on unsubscribe), identical to the prior FromAsync CT,
            // so REPL state + CancelScriptRequest semantics are preserved. The session owns
            // chain advancement (only on success — a throwing submission's variables are
            // discarded, the historical ScriptState behavior).
            // The external resolver reads the CURRENT bindings field (not a snapshot), so a
            // cell-surface set resolved after a transient first-submission failure still binds
            // for later submissions of the same session.
            //
            // 🚨 The script runs EXPLICITLY under its submission's identity, never under the
            // AsyncLocal the reactive chain happens to carry onto the pool. Upstream resolution
            // steps run System-scoped (the cell-surface provider's discovery query — whose
            // response emission the framework re-stamps with the OBSERVING caller's context by
            // design), and an inherited System identity here made a `--render` export resolve
            // embedded areas the submitting user may NOT read (DocumentExportAreaAccessTest).
            // A null delivery context leaves the ambient untouched — background flows that
            // impersonate explicitly keep their identity; nothing is invented.
            //
            // 🚨 TWO leaves, not one (#5388). The Roslyn half — build the submission, emit, load —
            // is pure computation and runs on the CPU lane's own threads (`cpuLane`), never a
            // ThreadPool worker; only the script's EXECUTION, which is user code that awaits, runs
            // as an async leaf — of the script lane, never the Compile pool, because the script
            // may wait on other async Compile leaves (another cell's kernel). As one async
            // leaf the whole emit ran on a pool worker before its first await
            // (Doc/Architecture/CompileOffTheThreadPool).
            scope => Observable.Defer(() =>
                {
                    // A module the script names — its entry assembly or one of its PRIVATE
                    // dependencies — binds the generation the session COMPILED against, through the
                    // module's own context (ModuleScriptBindings): modules run in their own
                    // collectible contexts (policy module-live-update-default), which the default
                    // context cannot see.
                    var bindings = moduleBindings;
                    var current = session ??= new ScriptSession(
                        scriptGlobals!,
                        name => (name.Name is { } simple ? cellSurfaceBindings?.GetValueOrDefault(simple) : null)
                                ?? bindings?.Bind(name));
                    var options = scriptOptions;
                    return cpuLane
                        .InvokeBlocking(t =>
                        {
                            var submission = current.Compile(cleaned, options, typeof(MeshScriptGlobals), t);
                            // Option C (Doc/Architecture/InMeshImpersonation): judge the submission's
                            // references to impersonation APIs BEFORE any of it runs. A script session is
                            // never trusted; LogOnly logs, Enforce refuses the cell. The scan covers
                            // THIS submission only: a continued script's compilation holds one syntax
                            // tree, its own — earlier cells are a referenced previous compilation — so a
                            // clean cell is never judged for an earlier cell's reference.
                            // GetRequiredService: every hub registers an AccessService; a missing one
                            // must fail the cell loudly, never switch the check off in silence.
                            if (publicHub.ServiceProvider.GetRequiredService<AccessService>().ImpersonationGuard is
                                { Mode: not InMeshImpersonationMode.Off } guard)
                                guard.CheckCompiled(ScriptSession.LoadContextName, publicHub.Address.ToString(),
                                    InMeshImpersonationReferences.Find(submission.Compilation, t)
                                        .Select(r => r.ToString()).ToList());
                            return submission;
                        })
                        .SelectMany(compiled => AdmitToScriptLane(t =>
                        {
                            // The scope is held only while the submission STARTS: its first await
                            // captures the ExecutionContext with the identity set, and the scope is
                            // then restored on THIS thread (the restore is thread-affine). Awaiting
                            // inside the using would dispose it on whichever thread resumed.
                            using (accessContext is null
                                       ? null
                                       : publicHub.ServiceProvider.GetService<AccessService>()
                                           ?.SwitchAccessContext(accessContext))
                                return current.ExecuteAsync(compiled, t);
                        }));
                })
                .Select(returnValue =>
                {
                    scope.Flush();
                    if (returnValue is not null)
                    {
                        UpdateView(viewId, returnValue);
                        // A CONTROL is already rendered by UpdateView above — logging it as well
                        // prints its record ToString() into the cell's output pane beside the very
                        // thing it describes ("StackControl { Id = , Style = , Skins = System.
                        // Collections.Immutable.ImmutableList`1[...] }"). Observed on
                        // RiskTransfer/01-GrossToNet under the rendered loss-book grid.
                        //
                        // Everything that DESCRIBES ITSELF still logs: for `1 + 1` or a string, that
                        // line IS the cell's output and the only thing the learner sees.
                        if (OutputLine(returnValue) is { } line)
                            scriptOutputLogger.LogInformation("{Value}", line);
                    }
                    return returnValue;
                }));
    }

    /// <summary>
    /// The transcript line a script's return value earns, or <c>null</c> when it earns none.
    /// <para>A control earns none (it is rendered, see above). Neither does a value whose
    /// <c>ToString()</c> is the one <see cref="object"/> gives everything — a dictionary, a list,
    /// an array, a plain class print their TYPE NAME
    /// (<c>System.Collections.Generic.Dictionary`2[System.String,System.Object]</c>), which tells
    /// the reader nothing about the result and became the activity page's headline. The value
    /// itself is not lost: it is serialized onto <c>ActivityLog.ReturnValue</c>, which is what a
    /// view renders.</para>
    /// </summary>
    internal static string? OutputLine(object returnValue)
    {
        if (returnValue is IUiControl) return null;
        // A string is its own text — even one that happens to spell "System.String".
        if (returnValue is string s) return s;
        var text = returnValue.ToString() ?? "";
        return text == returnValue.GetType().ToString() ? null : text;
    }

    private sealed class StdoutScope(LoggerTextWriter stdoutPipe, LoggerTextWriter stderrPipe, IDisposable capture) : IDisposable
    {
        /// <summary>Flushes any partially-buffered line on both pipes.</summary>
        public void Flush()
        {
            stdoutPipe.Flush();
            stderrPipe.Flush();
        }

        public void Dispose()
        {
            capture.Dispose();
            stdoutPipe.Dispose();
            stderrPipe.Dispose();
        }
    }

    /// <summary>
    /// Build script options once on first submission. Pulls the IMessageHub for
    /// the globals from the public hub (the parent), so scripts see the SAME hub
    /// external callers do — they can post messages, subscribe streams, etc.
    ///
    /// <para>🚨 Runs INSIDE the script-lane IoPool leaf <see cref="Execute"/> wraps it in — never bare
    /// on the ThreadPool as before (issues #2480/#2578). The reference materialization behind
    /// <see cref="MeshScriptEnvironment.ReferencesAsync"/> can be genuinely slow the first time
    /// any mesh in the process touches it, and used to hold no pool gate at all while doing so —
    /// invisible to <c>IoPoolRegistry.DrainAll</c> and therefore never cancelled or joined by
    /// teardown. Now the wait is a real, trackable, cancellable leaf; <paramref name="ct"/>
    /// governs only how long THIS session waits, never the shared snapshot build itself (see
    /// <see cref="KernelScriptReferences.GetReferencesAsync"/>).</para>
    /// </summary>
    private async Task<Unit> EnsureInitializedAsync(CancellationToken ct)
    {
        if (initialized) return Unit.Default;

        var defaultLogger = publicHub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.Kernel.Script");
        var localScriptLogger = new ScriptLogger(defaultLogger);
        var localScriptGlobals = new MeshScriptGlobals { Mesh = publicHub, Log = localScriptLogger };

        // Curated anchors + DI-contributed module assemblies + default imports all come
        // from MeshScriptEnvironment — the ONE description of the script environment,
        // shared with the language service's completion workspace so the editor suggests
        // exactly what the kernel can run. 🚨 Never pass raw Assembly objects to
        // WithReferences: Roslyn then materializes a fresh AssemblyMetadata + native
        // metadata block per reference PER SESSION (~350 refs ≈ 150-200 MiB of native
        // memory per kernel session, never reclaimed — the CI memory-pressure leak; see
        // KernelScriptReferences docs).
        //
        // 🚨 `initialized` flips to true ONLY after every step below has succeeded (Copilot
        // review, PR #2598). It used to be set FIRST — harmless when the reference build was a
        // synchronous call that either finished or threw synchronously right there, but this
        // method is now `await`-based and genuinely cancellable, so a cancellation or a
        // transient fault partway through used to leave `initialized == true` with
        // `scriptOptions` still null FOREVER: the guard at the top would then skip every future
        // submission's attempt to build it, permanently wedging the session. Local variables
        // here (rather than writing straight into the instance fields) keep a failed attempt
        // from leaving scriptLogger/scriptGlobals half-set either.
        // The module generations this session compiles against are PINNED here, and the run-time
        // bind resolves from the same set — so a live swap can never make a submission compile
        // against one generation and run against another (ModuleScriptBindings).
        var localModuleBindings = ModuleScriptBindings.PinCurrent(
            publicHub.ServiceProvider.GetService<ModuleContexts>());
        ImmutableArray<MetadataReference> references;
        try
        {
            references = await KernelScriptReferences
                .GetReferencesAsync(localModuleBindings.SessionAssemblies(publicHub.ServiceProvider), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            localModuleBindings.Dispose();
            throw;
        }
        // One definition of the session's options (resolver, imports, debug info) — shared with the
        // doc-cell compile guard, so what CI type-checks is what the kernel runs.
        var options = MeshScriptEnvironment.CreateScriptOptions(references);

        scriptLogger = localScriptLogger;
        scriptGlobals = localScriptGlobals;
        scriptOptions = options;
        moduleBindings = localModuleBindings;
        initialized = true;
        return Unit.Default;
    }

    /// <summary>
    /// The pack-scripting seam, session side (issue #1649 part 2): resolves the CURRENT baked
    /// assembly of every <c>cellSurface: true</c> NodeType through
    /// <see cref="ICellSurfaceAssemblyProvider"/> (implemented over the compilation cache in
    /// MeshWeaver.Graph), adds each PE as a metadata reference to this session's
    /// <see cref="scriptOptions"/> (compile-time visibility), and records name → Assembly in
    /// <see cref="cellSurfaceBindings"/> for the session load context's runtime bind — scoped to
    /// exactly the set declared here, never a blanket Default-ALC hook. Runs once per kernel
    /// session (the executor), on the serial Concat pump, so the plain field check is race-free;
    /// resolving per session rather than per process is what makes the surface immune to the
    /// frozen snapshot and to NodeType recompiles (old sessions keep their generation via the
    /// leases; new sessions bind the current one).
    ///
    /// <para>Degrades OPEN, loudly: when the provider is absent (no Graph module) the surface is
    /// simply the platform baseline; when resolution FAULTS, the failure is logged at Warning and
    /// this submission runs without pack references — but the failure is not latched, so the next
    /// submission re-attempts instead of replaying one transient fault for the session's life.</para>
    /// </summary>
    private IObservable<Unit> EnsureCellSurfaceReferences()
    {
        if (cellSurfaceBindings is not null)
            return Observable.Return(Unit.Default);

        var provider = publicHub.ServiceProvider.GetService<ICellSurfaceAssemblyProvider>();
        if (provider is null)
        {
            cellSurfaceBindings = ImmutableDictionary<string, Assembly>.Empty;
            return Observable.Return(Unit.Default);
        }

        return provider.ResolveCellSurfaceAssemblies()
            .Take(1)
            .Select(resolved =>
            {
                var bindings = ImmutableDictionary.CreateBuilder<string, Assembly>(StringComparer.Ordinal);
                var references = new List<MetadataReference>();
                foreach (var entry in resolved)
                {
                    var reference = KernelScriptReferences.GetOrCreateFromFile(entry.AssemblyPath);
                    if (reference is null)
                    {
                        // Unreadable PE (deleted mid-resolve) — drop the whole entry: a runtime
                        // bind without the metadata reference could never be requested anyway.
                        entry.Lease.Dispose();
                        Logger.LogWarning(
                            "Cell-surface assembly for {NodeTypePath} at {AssemblyPath} could not be materialized as a metadata reference — skipped",
                            entry.NodeTypePath, entry.AssemblyPath);
                        continue;
                    }
                    references.Add(reference);
                    if (entry.Assembly.GetName().Name is { Length: > 0 } name)
                        bindings[name] = entry.Assembly;
                    cellSurfaceLeases.Add(entry.Lease);
                }
                scriptOptions = scriptOptions.AddReferences(references);
                cellSurfaceBindings = bindings.ToImmutable();
                if (!cellSurfaceBindings.IsEmpty)
                    Logger.LogDebug(
                        "Kernel session joined {Count} cell-surface pack assembly(ies): {Names}",
                        cellSurfaceBindings.Count, string.Join(", ", cellSurfaceBindings.Keys));
                return Unit.Default;
            })
            .Catch<Unit, Exception>(ex =>
            {
                Logger.LogWarning(ex,
                    "Cell-surface assembly resolution failed — this submission runs without pack references; the next submission re-attempts");
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>
    /// Per-hub assembly probing resolver — NO static state (NoStaticState.md). The
    /// probing-dir set and the <see cref="AssemblyLoadContext.Default"/> Resolving
    /// hook live and die with the kernel hub: the hook is removed on hub disposal,
    /// so package dirs never bleed across meshes/tests.
    /// </summary>
    internal sealed class AssemblyProbingResolver : IDisposable
    {
        // One entry per resolution (a submission's ResolvedPackageSet.ProbingDirectories — managed
        // runtime dirs first, native last). Kept apart so a native import is answered only from the
        // closure its requesting assembly belongs to.
        private ImmutableList<ImmutableArray<string>> closures = ImmutableList<ImmutableArray<string>>.Empty;
        private readonly object gate = new();
        private readonly Func<AssemblyLoadContext, System.Reflection.AssemblyName, System.Reflection.Assembly?> handler;
        private readonly Func<System.Reflection.Assembly, string, IntPtr> nativeHandler;

        public AssemblyProbingResolver()
        {
            handler = (ctx, name) =>
            {
                // The default context only raises Resolving for a name the host does NOT ship, so
                // host unification is implicit here; the probe skips the native directories.
                if (string.IsNullOrEmpty(name.Name)) return null;
                ImmutableList<ImmutableArray<string>> current;
                lock (gate)
                    current = closures;
                var dirs = current.SelectMany(c => c).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in MeshWeaver.NuGet.PackageAssemblyProbe.FindManagedCandidates(name.Name, dirs, name.CultureName))
                {
                    try { return ctx.LoadFromAssemblyPath(candidate); }
                    catch { /* try the next candidate */ }
                }
                return null;
            };
            // A package's runtimes/<rid>/native/ library for a DllImport the default search missed —
            // answered ONLY from the FIRST resolution whose managed directories hold the requesting
            // assembly, so a later submission's (or another session's) different version of the same
            // native library is never offered to it. The default context is process-wide: one
            // managed path is one loaded assembly with one native binding, whoever asked first.
            nativeHandler = (requesting, libraryName) =>
            {
                if (requesting.IsDynamic) return IntPtr.Zero;
                ImmutableList<ImmutableArray<string>> current;
                lock (gate)
                    current = closures;
                var dirs = MeshWeaver.NuGet.PackageAssemblyProbe.NativeSearchDirectoriesFor(requesting.Location, current);
                return dirs.IsEmpty
                    ? IntPtr.Zero
                    : MeshWeaver.NuGet.PackageAssemblyProbe.ResolveUnmanaged(libraryName, dirs);
            };
            AssemblyLoadContext.Default.Resolving += handler;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += nativeHandler;
        }

        public void AddDirectories(IEnumerable<string> dirs)
        {
            var closure = dirs.ToImmutableArray();
            if (closure.IsEmpty) return;
            lock (gate)
                if (!closures.Any(c => c.SequenceEqual(closure, StringComparer.OrdinalIgnoreCase)))
                    closures = closures.Add(closure);
        }

        public void Dispose()
        {
            AssemblyLoadContext.Default.Resolving -= handler;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= nativeHandler;
        }
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

    // Reactive end to end: the resolver runs its NuGet leaf on the Http lane and bounds it itself, so
    // this is NOT wrapped in a pool slot of that same lane — a slot held while waiting on a leaf of its
    // own lane can starve it.
    private IObservable<string> ResolveNuGetReferences(string source)
    {
        var (cleaned, refs) = NuGetDirectiveParser.Extract(source);
        if (refs.Length == 0) return Observable.Return(source);

        var resolver = publicHub.ServiceProvider.GetService<INuGetAssemblyResolver>();
        if (resolver is null)
        {
            Logger.LogWarning("INuGetAssemblyResolver not registered; #r \"nuget:...\" directives ignored.");
            return Observable.Return(cleaned);
        }

        return resolver.Resolve(refs)
            .Select(resolved =>
            {
                // One assembly per name, the copy that runs (CompileReferences.WithPackageAssemblies):
                // a closure assembly the session already references is not added a second time. The
                // kernel's packages bind in the DEFAULT context, which cannot load a second version of a
                // host assembly — so the host's reference is kept even when the package's is newer.
                scriptOptions = scriptOptions.WithReferences(
                    MeshWeaver.Compiler.CompileReferences.WithPackageAssemblies(
                        [.. scriptOptions.MetadataReferences], resolved.AssemblyPaths,
                        KernelScriptReferences.GetOrCreateFromFile, packagesLoadInIsolation: false));
                InstallRuntimeProbe(resolved.ProbingDirectories);
                Logger.LogInformation("Resolved {Count} NuGet package(s) for interactive cell.", refs.Length);
                return cleaned;
            })
            .Do(_ => { }, ex => Logger.LogError(ex, "NuGet restore failed for interactive cell."));
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
/// The reason a run ends when the kernel executor hosting it is disposed — the Activity hub was
/// recycled, idled out after a crash, or the process is shutting down — before the run settled
/// (#7362). Recorded on the Activity under <see cref="MessageKey"/>.
/// </summary>
public sealed class KernelHostShutDownException()
    : Exception("The kernel host shut down while this run was in progress, so the run did not finish.")
{
    /// <summary>The catalog key of the transcript entry (<c>strings.{en,de}.json</c>).</summary>
    public const string MessageKey = "activity.codeRun.kernelHostShutDown";
}

/// <summary>
/// Wraps a Roslyn-script compile/runtime failure with the diagnostic message
/// surfaced to the caller. Distinct type so callers can pattern-match.
/// </summary>
public sealed class ScriptExecutionException(string message, Exception inner) : Exception(message, inner);
