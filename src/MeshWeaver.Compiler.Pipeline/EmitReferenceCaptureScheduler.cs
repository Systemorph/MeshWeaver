using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Optional CI diagnostics use the existing, drainable file pool. They never join
/// the compile's deadline and never retain source trees or an entire compilation.
/// </summary>
internal sealed class EmitReferenceCaptureScheduler(
    IIoPool pool, string directory, Action<IDisposable> registerForDisposal,
    EmitReferenceCaptureReservation reservation)
{
    internal static Action<CSharpCompilation, Exception>? ForCi(IMessageHub hub)
    {
        var directory = EmitReferenceCapture.GetCiDirectory(Environment.GetEnvironmentVariable);
        if (directory is null)
            return null;

        // Resolve while the service scope is alive, never from a deferred diagnostic.
        // No untracked/unbounded fallback: absent infrastructure means no capture.
        try
        {
            var registry = hub.ServiceProvider.GetService<IoPoolRegistry>();
            if (registry is null)
                return (_, error) => error.Data[EmitPipeline.EmitReferenceCaptureDataKey] =
                    "incomplete:no-file-pool";

            var reservation = hub.ServiceProvider.GetService<EmitReferenceCaptureReservation>();
            if (reservation is null)
                return (_, error) => error.Data[EmitPipeline.EmitReferenceCaptureDataKey] =
                    "incomplete:no-reservation";

            var scheduler = new EmitReferenceCaptureScheduler(registry.Get(IoPoolNames.FileSystem),
                directory, item => hub.RegisterForDisposal(item), reservation);
            return scheduler.Schedule;
        }
        catch (Exception)
        {
            // An opt-in diagnostic cannot prevent construction of the compiler
            // service when its optional diagnostic infrastructure is unavailable.
            return (_, error) => error.Data[EmitPipeline.EmitReferenceCaptureDataKey] =
                "incomplete:file-pool-unavailable";
        }
    }

    internal void Schedule(CSharpCompilation compilation, Exception error)
    {
        // Claim before enumerating references or registering/enqueueing work. All
        // compiler hubs in this mesh share the root-owned reservation. Never retry
        // a failed/cancelled capture during a cascade; the manifest remains incomplete.
        if (!reservation.TryReserve())
        {
            error.Data[EmitPipeline.EmitReferenceCaptureDataKey] = "incomplete:already-requested";
            return;
        }

        // Preserve only the reference sequence. No source, configuration, service
        // resolution or mesh mutation occurs in the I/O leaf.
        var references = compilation.References.ToArray();
        var failureType = error.GetType().FullName ?? error.GetType().Name;
        var captureDirectory = directory; // Do not close over this scheduler and its hub-owned registration delegate.
        var captureId = Guid.NewGuid().ToString("N");
        var owner = new SerialDisposable();
        try
        {
            registerForDisposal(owner);
            if (owner.IsDisposed)
            {
                error.Data[EmitPipeline.EmitReferenceCaptureDataKey] = "incomplete:owner-disposed";
                return;
            }
            error.Data[EmitPipeline.EmitReferenceCaptureDataKey] =
                $"requested id={captureId}; missing, mismatched or incomplete manifest is not a captured closure";
            owner.Disposable = pool.InvokeBlocking(ct =>
                    EmitReferenceCapture.Capture(references, captureDirectory, failureType, ct, captureId))
                // Errors can arrive asynchronously (including a draining pool).
                // The original emit error has already been reported unchanged.
                .Catch<string, Exception>(_ => Observable.Return("incomplete:io-pool"))
                .Subscribe(_ => owner.Dispose(), _ => owner.Dispose());
        }
        catch
        {
            owner.Dispose();
            throw; // The emit boundary records scheduling failure and rethrows its original.
        }
    }
}

/// <summary>Root-service-owned once gate; no static state or actor-side file I/O.</summary>
internal sealed class EmitReferenceCaptureReservation
{
    private int requested;

    internal bool TryReserve() => Interlocked.CompareExchange(ref requested, 1, 0) == 0;
}
