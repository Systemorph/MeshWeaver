using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
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
    /// Sync entry — kicks off the script work via <see cref="Observable.FromAsync"/>
    /// so the executor's action block isn't blocked while the script runs.
    /// Concurrency on <see cref="scriptState"/> is serialised by <see cref="executionLock"/>.
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

        Observable.FromAsync(_ => ExecuteAsync(hub, msg.Code, msg.Id, activityLogger, cts.Token))
            .Subscribe(
                _ =>
                {
                    ClearCancellationIf(cts);
                    activityLogger.Complete(ActivityStatus.Succeeded);
                    hub.Post(new SubmitCodeResponse(msg.Id, true), o => o.ResponseFor(request));
                },
                ex =>
                {
                    ClearCancellationIf(cts);
                    var canceled = ex is OperationCanceledException;
                    if (canceled)
                        activityLogger.LogWarning("Script execution cancelled by user");
                    else
                        activityLogger.LogError(ex, "Script dispatch failed");
                    activityLogger.Complete(canceled ? ActivityStatus.Failed : ActivityStatus.Failed);
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

    private async Task ExecuteAsync(
        IMessageHub hub,
        string code,
        string viewId,
        ILogger scriptOutputLogger,
        CancellationToken ct)
    {
        await executionLock.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            var cleaned = await ResolveNuGetReferencesAsync(code, ct);

            // Swap the script logger for this submission so user-script
            // Log.LogInformation lands on the requested ActivityLog.
            scriptLogger!.Set(scriptOutputLogger);
            // Rebind the per-submission CancellationToken on the shared globals
            // object so `Task.Delay(ms, Ct)` etc. inside the script see THIS
            // submission's cancellation source.
            scriptGlobals!.Ct = ct;

            // Capture stdout into the same logger so Console.WriteLine ends up
            // on the activity log alongside Log.LogInformation calls.
            using var stdoutPipe = new LoggerTextWriter(scriptOutputLogger);
            using (CapturingTextWriter.Capture(stdoutPipe))
            {
                if (scriptState is null)
                {
                    scriptState = await CSharpScript.RunAsync(
                        cleaned, scriptOptions, scriptGlobals, typeof(MeshScriptGlobals), ct);
                }
                else
                {
                    scriptState = await scriptState.ContinueWithAsync(cleaned, scriptOptions, ct);
                }
                stdoutPipe.Flush();
            }

            if (scriptState.ReturnValue is not null)
            {
                var value = scriptState.ReturnValue;
                UpdateView(viewId, value);
                scriptOutputLogger.LogInformation("{Value}", value.ToString() ?? "");
            }
        }
        catch (CompilationErrorException ex)
        {
            var msg = string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
            UpdateView(viewId, Controls.Markdown($"**Execution failed**:\n{msg}"));
            throw new ScriptExecutionException(msg, ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            UpdateView(viewId, Controls.Markdown($"**Execution failed**:\n{ex.Message}"));
            throw;
        }
        finally
        {
            executionLock.Release();
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
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            if (string.IsNullOrEmpty(asm.Location)) continue;
            refs.Add(asm);
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
                "Microsoft.Extensions.Logging",
                "MeshWeaver.Application.Styles",
                "MeshWeaver.Layout",
                "MeshWeaver.Layout.DataGrid",
                "MeshWeaver.Messaging"
            )
            .WithEmitDebugInformation(true);
    }

    private static readonly HashSet<string> _probingDirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _probingDirLock = new();
    private static bool _probingResolverInstalled;

    private static void InstallRuntimeProbe(IEnumerable<string> dirs)
    {
        lock (_probingDirLock)
        {
            foreach (var dir in dirs) _probingDirs.Add(dir);
            if (_probingResolverInstalled) return;
            _probingResolverInstalled = true;

            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                var dllName = name.Name + ".dll";
                lock (_probingDirLock)
                {
                    foreach (var d in _probingDirs)
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
        }
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
