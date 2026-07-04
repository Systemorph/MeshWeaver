using System.Runtime.CompilerServices;

namespace MeshWeaver.Fixture;

/// <summary>
/// Names the teardown straggler behind the "green-but-catastrophic" CI shard failures:
/// <c>Catastrophic failure: System.ObjectDisposedException : Instances cannot be resolved and
/// nested lifetimes cannot be created from this LifetimeScope …</c> with 123/123 passed yet exit 1.
///
/// <para><b>The escape channel is NOT the unobserved-task path.</b> xunit v3 (3.2.2) never hooks
/// <see cref="TaskScheduler.UnobservedTaskException"/> — verified against the shipped binaries
/// (<c>xunit.v3.runner.inproc.console.dll</c> references <c>add_UnhandledException</c> only, no
/// <c>UnobservedTask</c>) — and .NET Core does not escalate unobserved task exceptions
/// (<c>ThrowUnobservedTaskExceptions</c> is off). The producer of the runner <c>ErrorMessage</c>
/// reported as "Catastrophic failure: {0}" is xunit's <see cref="AppDomain.UnhandledException"/>
/// hook in the in-proc console runner (<c>Xunit.Runner.InProc.SystemConsole.ConsoleRunner</c>:
/// <c>AppDomain.CurrentDomain.UnhandledException += … logger.WriteMessage(ErrorMessage.FromException(ex))</c>).
/// It records message only, NO stack — hence "anonymous". That is why the two
/// UnobservedTaskException-based suppressors (<see cref="TestTeardownExceptionSuppressor"/>, the
/// Orleans-local one) never stopped it: wrong event.</para>
///
/// <para><b>The named root (task #20), by this capturer's first-chance stack.</b> The disposed-scope
/// ODE is thrown from Orleans' own connection message pump —
/// <c>Orleans.Runtime.Messaging.Connection.ProcessIncoming</c> → <c>MessageSerializer.TryRead</c> →
/// <c>CodecProvider.TryCreateCodec</c> → <c>ActivatorUtilities.CreateInstance</c> →
/// <c>AutofacServiceProvider.GetService</c> on a disposed <c>LifetimeScope</c>. A silo/client
/// connection is still deserializing an in-flight message while its Autofac container is torn down
/// during <c>TestCluster</c> teardown. Purely Orleans-internal (the #231 mesh drain never covered it);
/// fixed at the disposal seam — <c>OrleansClusterDisposal</c> now runs the graceful
/// <c>StopAllSilosAsync()</c> (which drains the pumps) BEFORE <c>DisposeAsync()</c>. This capturer
/// stays as the permanent net so any future straggler fails with a named stack, not folklore.</para>
///
/// <para><b>What this capturer does</b> — record, never suppress:</para>
/// <list type="number">
///   <item><see cref="AppDomain.FirstChanceException"/>, filtered STRICTLY to the teardown
///   disposed-resource <see cref="ObjectDisposedException"/> shapes (Autofac <c>LifetimeScope</c> +
///   mesh-owned <c>MemoryCache</c> — see <see cref="IsTeardownDisposedStraggler"/>): records the full
///   throw-site stack (<see cref="Environment.StackTrace"/> — at first-chance time the exception's own
///   StackTrace has only the throw frame) + thread/task context. This fires even when the runtime
///   later CATCHES the throw (Orleans caught all 137/137 in a green run), so the throw-site is named
///   without needing the rare unobserved escape. Bounded (cap, cheap filter), so it can live on
///   permanently.</item>
///   <item><see cref="AppDomain.UnhandledException"/> — the channel that actually reds the shard:
///   records the COMPLETE exception (full stack, by now unwound to the callback root) to the
///   test-logs file AND stderr, right next to xunit's anonymous catastrophic line. Future stragglers
///   are named failures, not folklore. The run still fails — nothing is swallowed here.</item>
///   <item><see cref="TaskScheduler.UnobservedTaskException"/>, same strict filter — diagnostics
///   only; observation policy stays in <see cref="TestTeardownExceptionSuppressor"/>.</item>
/// </list>
///
/// <para>Output: <c>{BaseDirectory}/test-logs/teardown-stragglers.log</c> — the CI log-collection
/// step already globs <c>*/bin/*/test-logs/*.log</c> into the run artifacts, so every capture ships
/// automatically.</para>
///
/// <para>NoStaticState note: this is process-lifetime diagnostics infrastructure hooked on
/// process-wide CLR events — there is no mesh to scope it to. It holds no collections; the log FILE
/// is the record, the only mutable statics are bounded counters.</para>
/// </summary>
internal static class TeardownStragglerCapturer
{
    private const int FirstChanceCap = 200;

    private static int firstChanceCount;
    private static int unhandledCount;
    private static int unobservedCount;
    private static readonly object WriteLock = new();

    private static string LogPath =>
        Path.Combine(AppContext.BaseDirectory, "test-logs", "teardown-stragglers.log");

    [ModuleInitializer]
    public static void Init()
    {
        AppDomain.CurrentDomain.FirstChanceException += static (_, e) =>
        {
            // Strict filter — only the teardown disposed-resource shapes (Autofac LifetimeScope +
            // MemoryCache); everything else is untouched. Recursion safety: nothing our own
            // recording code can throw matches this filter.
            if (!IsTeardownDisposedStraggler(e.Exception))
                return;
            var n = Interlocked.Increment(ref firstChanceCount);
            if (n > FirstChanceCap)
                return;
            // Environment.StackTrace at first-chance time = throw site + callers (the exception's
            // own StackTrace is not yet unwound past the throw frame).
            Append(
                $"FIRST-CHANCE #{n} {Header()}\n{e.Exception.GetType().FullName}: {e.Exception.Message}\n{Environment.StackTrace}\n"
                + (n == FirstChanceCap ? "── first-chance capture cap reached; further matches counted but not dumped ──\n" : ""));
        };

        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            // THIS is the escape that reds a green shard as an anonymous "Catastrophic failure":
            // xunit's own hook forwards only the MESSAGE. Name it — full stack, thread context —
            // in the CI-collected log AND on stderr. Never swallows; the run still fails.
            var n = Interlocked.Increment(ref unhandledCount);
            var text =
                $"UNHANDLED #{n} (terminating={e.IsTerminating}) {Header()}\n"
                + "── this exception is what xunit reports as the anonymous 'Catastrophic failure' ──\n"
                + (e.ExceptionObject as Exception)?.ToString()
                + $"\n(first-chance disposed-resource captures so far: {Volatile.Read(ref firstChanceCount)})\n";
            Append(text);
            try
            {
                Console.Error.WriteLine($"[TeardownStragglerCapturer] {text}");
            }
            catch
            {
                // stderr may be gone at process teardown — the file capture above is the record
            }
        };

        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            // Diagnostics only — whether a benign shape gets observed stays the business of
            // TestTeardownExceptionSuppressor. Unobserved task exceptions never terminate on
            // .NET Core, so these captures explain history, not shard exit codes.
            var inners = e.Exception?.Flatten().InnerExceptions;
            if (inners is not { Count: > 0 } || !inners.Any(IsTeardownDisposedStraggler))
                return;
            var n = Interlocked.Increment(ref unobservedCount);
            Append($"UNOBSERVED-TASK #{n} {Header()}\n{e.Exception}\n");
        };
    }

    /// <summary>
    /// The teardown disposed-resource shapes this capturer names: a late continuation touching a
    /// resource whose owning scope has already been torn down. Two known shapes, both
    /// <see cref="ObjectDisposedException"/>: (1) the Autofac root container — the Orleans connection
    /// message pump resolving a codec from a disposed <c>LifetimeScope</c> (task #20, fixed at
    /// <c>OrleansClusterDisposal</c>); (2) a mesh-owned <c>MemoryCache</c> — a late write/read hitting
    /// e.g. <c>MeshNodeStreamCache._updateQueues</c> after the cache hub disposed. Both are the same
    /// "graceful terminal, never an unobserved throw" defect class (#228/#231 doctrine).
    /// </summary>
    private static bool IsTeardownDisposedStraggler(Exception? ex) =>
        IsAutofacDisposedScope(ex) || IsDisposedMemoryCache(ex);

    private static bool IsAutofacDisposedScope(Exception? ex) =>
        ex is ObjectDisposedException
        && (ex.Message?.Contains("LifetimeScope", StringComparison.OrdinalIgnoreCase) == true
            || ex.Message?.Contains("nested lifetimes cannot be created", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsDisposedMemoryCache(Exception? ex) =>
        ex is ObjectDisposedException ode
        && (string.Equals(ode.ObjectName, "Microsoft.Extensions.Caching.Memory.MemoryCache", StringComparison.Ordinal)
            || ode.Message?.Contains("MemoryCache", StringComparison.OrdinalIgnoreCase) == true);

    private static string Header()
    {
        var t = Thread.CurrentThread;
        return $"utc={DateTime.UtcNow:O} thread={t.ManagedThreadId} name='{t.Name}' pool={t.IsThreadPoolThread} "
               + $"taskId={Task.CurrentId?.ToString() ?? "-"} syncCtx={SynchronizationContext.Current?.GetType().Name ?? "-"}";
    }

    private static void Append(string text)
    {
        // A capturer must never take the run down: swallow ONLY our own IO faults here (this is
        // diagnostics hardening, not suppression of product errors — nothing product-side is caught).
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"════ {text}\n");
            }
        }
        catch
        {
            // disk unavailable at teardown — nothing further we can do
        }
    }
}
