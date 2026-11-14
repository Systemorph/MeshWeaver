using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MeshWeaver.Hosting;

/// <summary>
/// What the ThreadPool's queue HELD at the moment of a <c>[POOLSTALL]</c> capture, grouped by the code
/// each queued item will run (MeshWeaver#5388, MeshWeaver#5799). The stacks say what the workers were
/// doing; this says what the backlog they were draining IS — the producer of the queue.
///
/// <para>🚨 <b>Why the stacks were not enough.</b> The 2026-11-11 14:40:57Z capture on memex-cloud read
/// 69,012 queued items over 42 threads and printed its largest shape, <c>×20</c> of 44 workers, as the
/// pool's own idle wait (<c>LowLevelLifoSemaphore.WaitForSignal</c>). A pool that has idle threads and a
/// five-second queue is not short of threads and is not blocked: it is draining a backlog at the
/// concurrency it chose, and no worker stack can say what that backlog consists of — every item in it is
/// still waiting to run. The previous reading took the idle shape for twenty busy hub handlers and asked
/// for "a capture that keeps the handler's message type"; the item that names the producer is not on any
/// stack at all. It is in the queue, and this reads the queue.</para>
///
/// <para><b>How.</b> The runtime's debugger enumeration of queued work items,
/// <c>ThreadPool.GetQueuedWorkItems()</c> (internal; the same walk SOS's <c>!ThreadPoolQueue</c> does over
/// a full dump). It is reached by reflection because no public API exposes the queue, and it is
/// deliberately allowed to be missing: a runtime without it yields a census that SAYS so, and the stall
/// line still prints. Each item is keyed by the delegate it will invoke — a <see cref="Task"/>'s action,
/// an async method's state machine, or the first delegate a work-item wrapper holds — so a hub drain reads
/// <c>MeshWeaver.Messaging.MessageService.DrainOne</c> and an awaited continuation reads its method.</para>
///
/// <para><b>Cost.</b> One pass over the queue on the heartbeat's capture thread, bounded by
/// <see cref="MaxItems"/>, at most once per <see cref="ThreadPoolStall.CaptureCooldown"/>. It allocates
/// nothing in the queue and runs nothing from it.</para>
/// </summary>
/// <param name="Groups">The producers, largest first: the key and how many queued items it accounts for.</param>
/// <param name="Read">How many items were read.</param>
/// <param name="Cut">Whether the walk stopped at <see cref="MaxItems"/> with more items still queued.</param>
/// <param name="Failure">Why the queue could not be read, or null when it was.</param>
public sealed record ThreadPoolQueueCensus(
    ImmutableArray<(string Producer, int Count)> Groups, int Read, bool Cut, string? Failure)
{
    /// <summary>The most queued items one census reads; the rest are reported as not read.</summary>
    public const int MaxItems = 500_000;

    /// <summary>How many producer groups the line prints, largest first; the rest are counted.</summary>
    public const int MaxGroups = 15;

    /// <summary>The runtime's own enumeration of the queued items, or null on a runtime without it.</summary>
    private static readonly MethodInfo? QueuedWorkItems = typeof(ThreadPool).GetMethod(
        "GetQueuedWorkItems", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);

    /// <summary>Whether this runtime exposes the enumeration the census reads.</summary>
    public static bool IsAvailable => QueuedWorkItems is not null;

    /// <summary>
    /// Reads the real ThreadPool's queue. Never throws: a queue that cannot be read becomes a census
    /// whose <see cref="Failure"/> says why.
    /// </summary>
    /// <returns>The census.</returns>
    public static ThreadPoolQueueCensus Take()
    {
        if (QueuedWorkItems is null)
            return Unavailable("this runtime has no ThreadPool.GetQueuedWorkItems()");
        try
        {
            return QueuedWorkItems.Invoke(null, null) is IEnumerable<object> items
                ? Of(items, MaxItems)
                : Unavailable("ThreadPool.GetQueuedWorkItems() answered no sequence");
        }
        catch (Exception exception)
        {
            var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            return Unavailable($"{cause.GetType().Name}: {cause.Message}");
        }
    }

    /// <summary>
    /// The census of a given sequence of queued items — the production walk's grouping, and what the
    /// tests drive with items whose producers they know.
    /// </summary>
    /// <param name="items">The queued work items.</param>
    /// <param name="maxItems">The most items to read.</param>
    /// <returns>The census.</returns>
    public static ThreadPoolQueueCensus Of(IEnumerable<object> items, int maxItems)
        => Of(items, maxItems, linkResolver: null);

    /// <summary>
    /// <see cref="Of(IEnumerable{object}, int)"/> with the runtime-async link resolver replaceable,
    /// so a test can make the runtime-private read THROW and see that one item reports it while
    /// the census goes on.
    /// </summary>
    /// <param name="items">The queued work items.</param>
    /// <param name="maxItems">The most items to read.</param>
    /// <param name="linkResolver">Maps one continuation link to the method it resumes, or null for the runtime's own.</param>
    /// <returns>The census.</returns>
    internal static ThreadPoolQueueCensus Of(IEnumerable<object> items, int maxItems, Func<object, MethodBase?>? linkResolver)
    {
        var read = 0;
        var cut = false;
        var counts = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null)
                continue;
            if (read == maxItems)
            {
                cut = true;
                break;
            }
            read++;
            var producer = ProducerOf(item, linkResolver);
            counts[producer] = counts.GetValueOrDefault(producer) + 1;
        }

        var groups = counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value))
            .ToImmutableArray();
        return new ThreadPoolQueueCensus(groups, read, cut, null);
    }

    private static ThreadPoolQueueCensus Unavailable(string why) => new([], 0, false, why);

    /// <summary>
    /// The code a queued item will run: a <see cref="Task"/>'s delegate, an async method's state machine,
    /// or — for a work-item wrapper (<c>QueueUserWorkItemCallback</c>, a continuation) — the wrapper's type
    /// and the first delegate it holds. Falls back to the item's type.
    /// </summary>
    /// <param name="item">One queued work item.</param>
    /// <param name="linkResolver">Maps one runtime-async continuation link to the method it resumes,
    /// or null for the runtime's own (<see cref="RuntimeAsync"/>); replaceable for tests only.</param>
    /// <returns>Its producer key.</returns>
    internal static string ProducerOf(object item, Func<object, MethodBase?>? linkResolver = null)
    {
        var type = item.GetType();
        if (item is Task)
        {
            if (RuntimeAsync.IsRuntimeAsyncTask(type))
                return RuntimeAsync.ProducerOf(item, linkResolver);
            if (FieldValue(item, typeof(Task), "m_action") is Delegate action)
                return MethodOf(action);
            return StateMachineOf(type) is { } machine ? Display(machine) : Display(type);
        }

        if (item is Delegate direct)
            return MethodOf(direct);
        return FirstDelegate(item) is { } held ? $"{Display(type)} → {MethodOf(held)}" : Display(type);
    }

    /// <summary>
    /// 🚨 <b>Runtime Async (.NET 11).</b> The runtime libraries are compiled with
    /// <c>runtime-async=on</c>, so a BCL async method (<c>Stream.CopyToAsync</c>,
    /// <c>StreamReader.ReadToEndAsync</c>, …) is no longer an <c>AsyncStateMachineBox</c>. Its
    /// task is an <c>AsyncHelpers+RuntimeAsyncTask&lt;T&gt;</c> whose <c>m_action</c> is the
    /// runtime's own <c>DispatchContinuations</c>. Keyed by that delegate, every such item of every
    /// producer collapsed into one group per RESULT TYPE. Measured on 11.0.0: four
    /// <c>Stream.CopyToAsync</c> continuations and five continuations of our own runtime-async
    /// methods all read <c>RuntimeAsyncTask`1[VoidTaskResult].DispatchContinuations</c>.
    ///
    /// <para>The method lives in the task's continuation chain instead (<c>m_stateObject</c>, then
    /// <c>Continuation.Next</c> link by link). Each link carries a <c>ResumeInfo*</c> whose
    /// <c>DiagnosticIP</c> is an address inside the method that will resume, and the runtime maps
    /// it back with <c>StackFrame.GetMethodFromNativeIP</c>, the same address the debugger and SOS
    /// use. All of it is internal and reached by reflection, and all of it is allowed to be
    /// missing: a runtime without it keys the item as an unresolved runtime-async continuation and
    /// SAYS so, rather than keying it as the dispatcher.</para>
    ///
    /// <para>🚨 <c>GetType()</c> on a continuation object throws <see cref="NotSupportedException"/>
    /// ("not supported for continuations"), so the chain is walked through the base type's
    /// <see cref="FieldInfo"/>s only.</para>
    ///
    /// <para>🚨 A runtime-private read that THROWS (the pointer read, the native-IP lookup) costs
    /// that ONE item its name, never the census: the item is keyed
    /// <c>runtime-async continuation (method unresolved: &lt;ExceptionType&gt;: &lt;message&gt;)</c>
    /// and the walk goes on. The census is a diagnostic over state the runtime does not promise to
    /// keep, so a fault there is a fact to report, not a reason to lose every other producer.</para>
    /// </summary>
    private static class RuntimeAsync
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const int MaxLinks = 64;
        private const string Unresolved = "runtime-async continuation (method unresolved: ";

        /// <summary>The runtime internals the chain is read through, every one present.</summary>
        private sealed record Internals(
            FieldInfo Next, FieldInfo ResumeInfo, MethodInfo PointerValue, MethodInfo MethodFromNativeIp, int DiagnosticIpOffset);

        /// <summary>The internals on this runtime, or why they cannot be had.</summary>
        private static readonly (Internals? Found, string? Missing) Resolved = Resolve();

        private static (Internals? Found, string? Missing) Resolve()
        {
            var coreLib = typeof(object).Assembly;
            if (coreLib.GetType("System.Runtime.CompilerServices.Continuation") is not { } continuation)
                return (null, "no Continuation type");
            if (continuation.GetField("Next", Instance) is not { } next
                || continuation.GetField("ResumeInfo", Instance) is not { } resumeInfo)
                return (null, "Continuation lacks Next/ResumeInfo");
            if (typeof(Pointer).GetMethod("GetPointerValue", Instance, Type.EmptyTypes) is not { } pointerValue)
                return (null, "no Pointer.GetPointerValue");
            if (typeof(StackFrame).GetMethod("GetMethodFromNativeIP",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(IntPtr)]) is not { } fromIp)
                return (null, "no StackFrame.GetMethodFromNativeIP");
            if (coreLib.GetType("System.Runtime.CompilerServices.ResumeInfo") is not { } resumeInfoType
                || resumeInfoType.GetField("DiagnosticIP", Instance) is not { FieldType.IsPointer: true })
                return (null, "no ResumeInfo.DiagnosticIP field");
            try
            {
                var offset = (int)Marshal.OffsetOf(resumeInfoType, "DiagnosticIP");
                return (new Internals(next, resumeInfo, pointerValue, fromIp, offset), null);
            }
            catch (ArgumentException exception)
            {
                return (null, $"ResumeInfo.DiagnosticIP is present but its layout is unreadable ({exception.Message})");
            }
        }

        /// <summary>Whether <paramref name="type"/> is the runtime's task for a runtime-async method.</summary>
        public static bool IsRuntimeAsyncTask(Type type) =>
            type is { IsGenericType: true, Name: "RuntimeAsyncTask`1", DeclaringType.FullName: "System.Runtime.CompilerServices.AsyncHelpers" };

        /// <summary>The method a runtime-async task resumes into first, or an unresolved key that says why.</summary>
        /// <param name="task">The runtime-async task.</param>
        /// <param name="linkResolver">Replaces the per-link read; null for the runtime's own.</param>
        public static string ProducerOf(object task, Func<object, MethodBase?>? linkResolver)
        {
            if (Resolved is not { Found: { } internals })
                return $"{Unresolved}{Resolved.Missing})";
            var methodAt = linkResolver ?? (link => MethodAt(internals, link));
            try
            {
                var link = FieldValue(task, typeof(Task), "m_stateObject");
                for (var hops = 0; link is not null && hops < MaxLinks; hops++)
                {
                    if (methodAt(link) is { } method)
                        return $"{(method.DeclaringType is { } owner ? Display(owner) : "?")}.{method.Name}";
                    link = internals.Next.GetValue(link);
                }
                return $"{Unresolved}no link carried a diagnostic address)";
            }
            catch (Exception exception)
            {
                var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                return $"{Unresolved}{cause.GetType().Name}: {cause.Message})";
            }
        }

        private static MethodBase? MethodAt(Internals internals, object link)
        {
            if (internals.ResumeInfo.GetValue(link) is not Pointer boxed
                || internals.PointerValue.Invoke(boxed, null) is not IntPtr info || info == IntPtr.Zero)
                return null;
            var ip = Marshal.ReadIntPtr(info, internals.DiagnosticIpOffset);
            return ip == IntPtr.Zero ? null : internals.MethodFromNativeIp.Invoke(null, [ip]) as MethodBase;
        }
    }

    /// <summary>The state-machine type an async method's box carries as its last generic argument.</summary>
    private static Type? StateMachineOf(Type box)
    {
        for (var current = box; current is not null; current = current.BaseType)
            if (current is { IsGenericType: true, Name: var name } && name.StartsWith("AsyncStateMachineBox", StringComparison.Ordinal))
                return current.GetGenericArguments()[^1];
        return null;
    }

    private static object? FieldValue(object item, Type declaring, string name) =>
        declaring.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(item);

    private static Delegate? FirstDelegate(object item)
    {
        for (var type = item.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (typeof(Delegate).IsAssignableFrom(field.FieldType) && field.GetValue(item) is Delegate value)
                    return value;
        return null;
    }

    private static string MethodOf(Delegate action) =>
        $"{(action.Method.DeclaringType is { } owner ? Display(owner) : "?")}.{action.Method.Name}";

    /// <summary><see cref="Type.ToString"/>: the namespace-qualified name with generic arguments and no assembly names.</summary>
    private static string Display(Type type) => type.ToString();

    /// <summary>The census as the <c>[POOLSTALL]</c> line's last section.</summary>
    /// <returns>The rendered section, starting on a new line.</returns>
    public string Render()
    {
        var text = new StringBuilder().Append(Environment.NewLine).Append("  QUEUED WORK: ");
        if (Failure is not null)
            return text.Append("the queue could NOT be read (").Append(Failure)
                .Append("), so this line cannot name what the backlog consisted of.").ToString();

        text.Append(CultureInfo.InvariantCulture, $"{Read} item(s) read")
            .Append(Cut ? string.Create(CultureInfo.InvariantCulture, $" — the walk stopped at {MaxItems}, more were queued") : "")
            .Append(CultureInfo.InvariantCulture, $", in {Groups.Length} producer(s), largest first (keyed by the code each item will run):");
        foreach (var (producer, count) in Groups.Take(MaxGroups))
            text.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture, $"    ×{count} {producer}");
        if (Groups.Length > MaxGroups)
            text.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture,
                $"    … {Groups.Length - MaxGroups} more producer(s) holding {Groups.Skip(MaxGroups).Sum(group => group.Count)} item(s).");
        return text.ToString();
    }
}
