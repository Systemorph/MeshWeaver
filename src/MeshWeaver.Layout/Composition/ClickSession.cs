using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// ONE running click on ONE control of ONE layout-area stream — the owner-side state machine behind
/// every framework button's busy state (<c>Doc/GUI/ButtonPendingState</c>).
/// <para><b>Lifecycle.</b> Opened by <c>LayoutAreaHost.OnClick</c>, which writes
/// <see cref="ClickProgress.Running"/> = true before the action runs. The click's receipt is answered
/// when the ACTION settles (exactly as before: complete → accepted, error → refused); the session itself
/// settles when the action AND every source handed to <see cref="Track"/> settled — that is what keeps a
/// button busy over an activity the click started.</para>
/// <para><b>Idempotence.</b> While the session is open, a second <see cref="ClickedEvent"/> for the same
/// key (area + row + payload) does NOT run the action again: it JOINS this session and its receipt is
/// answered with the same outcome (<see cref="Join"/>).</para>
/// <para><b>Threading.</b> Every state transition runs on the stream hub's action block: the host calls
/// <see cref="Start"/> and <see cref="Join"/> from its handler, and every callback arriving on another
/// thread (the action's completion, a tracked source, a progress report, the cancel watch) is marshalled
/// there through <c>LayoutAreaHost.InvokeAsync</c> — serialization through the hub, no lock, no gate.</para>
/// </summary>
internal sealed class ClickSession : IDisposable
{
    private readonly LayoutAreaHost host;
    private readonly ILogger logger;
    private readonly Action<ClickSession> onSettled;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CompositeDisposable subscriptions = new();
    private readonly SerialDisposable actionSubscription = new();
    private readonly int sequence;
    private readonly AccessContext? clicker;
    private bool disposed;

    private ImmutableList<IMessageDelivery<ClickedEvent>> receipts = [];
    private ImmutableList<Action> cancelHandlers = [];
    private ImmutableDictionary<int, IDisposable> tracks = ImmutableDictionary<int, IDisposable>.Empty;
    private ImmutableHashSet<int> followThroughCancel = [];
    private int nextTrack;
    private bool actionDone;
    private bool settled;
    private string? receiptRefusal;
    private bool receiptAnswered;
    private ClickProgress progress = new() { Running = true, Cancellable = true };

    internal ClickSession(LayoutAreaHost host, string key, string progressArea, int sequence,
        ILogger logger, Action<ClickSession> onSettled)
    {
        this.host = host;
        Key = key;
        ProgressArea = progressArea;
        this.sequence = sequence;
        this.logger = logger;
        this.onSettled = onSettled;
        clicker = host.ViewerAccess?.Context;
    }

    /// <summary>Dedupe key: area + row + payload.</summary>
    internal string Key { get; }

    /// <summary>The area (row-qualified for row-scoped controls) whose <see cref="ClickProgress"/> this session writes.</summary>
    internal string ProgressArea { get; }

    /// <summary>Trips when — and only when — the viewer cancels; handed to the action as <c>ctx.CancellationToken</c>.</summary>
    internal CancellationToken Token => cancellation.Token;

    /// <summary>Writes the opening busy state and starts watching the cancel request. Runs on the hub.</summary>
    internal void Start(IMessageDelivery<ClickedEvent> request)
    {
        receipts = receipts.Add(request);
        host.UpdateData(ClickProgress.CancelDataId(ProgressArea), new ClickCancellation { Session = sequence });
        Write();
        subscriptions.Add(host.GetDataStream<ClickCancellation>(ClickProgress.CancelDataId(ProgressArea))
            .Where(c => c is { Requested: true } && c.Session == sequence)
            .Take(1)
            .Subscribe(
                _ => OnHub(Cancel),
                ex => logger.LogWarning(ex, "Cancel watch for click on {Area} failed", ProgressArea)));
    }

    /// <summary>Subscribes the action's completion; its terminal signal answers the receipts. Runs on the hub.</summary>
    internal void RunAction(IObservable<Unit> completion)
    {
        actionSubscription.Disposable = completion.Subscribe(
            _ => { },
            ex => OnHub(() => Fail(ex)),
            () => OnHub(ActionCompleted));
    }

    /// <summary>A duplicate click while this one runs: never re-run, answer it with the same outcome. Runs on the hub.</summary>
    internal void Join(IMessageDelivery<ClickedEvent> request)
    {
        logger.LogInformation(
            "Duplicate click on {Area} of {Hub} while the first is still running — joined, not re-run",
            request.Message.Area, host.Hub.Address);
        if (receiptAnswered)
            Answer(request, receiptRefusal);
        else
            receipts = receipts.Add(request);
    }

    /// <summary>A failure before the action could even be subscribed (it threw synchronously).</summary>
    internal void FailSynchronously(Exception exception) => Fail(exception);

    // ── surface used by UiActionContext ──────────────────────────────────────────────────────────

    internal void ReportProgress(string? status, double? fraction) => OnHub(() =>
    {
        if (settled) return;
        progress = progress with { Status = status ?? progress.Status, Fraction = fraction ?? progress.Fraction };
        Write();
    });

    internal void ReportSummary(string summary) => OnHub(() =>
    {
        progress = progress with { Summary = summary };
        if (!settled) Write();
    });

    internal void OnCancel(Action handler) => OnHub(() =>
    {
        if (settled) return;
        cancelHandlers = cancelHandlers.Add(handler);
    });

    internal void Track(IObservable<ClickProgress> source, bool followCancel) => OnHub(() =>
    {
        if (settled) return;
        var id = nextTrack++;
        if (followCancel) followThroughCancel = followThroughCancel.Add(id);
        var subscription = new SingleAssignmentDisposable();
        tracks = tracks.SetItem(id, subscription);
        subscription.Disposable = source.Subscribe(
            update => OnHub(() => OnTrackUpdate(id, update)),
            ex => OnHub(() => Fail(ex)),
            () => OnHub(() => EndTrack(id)));
    });

    // ── state transitions (on the hub) ───────────────────────────────────────────────────────────

    private void OnTrackUpdate(int id, ClickProgress update)
    {
        if (settled || !tracks.ContainsKey(id)) return;
        if (update.Error is { } error)
        {
            Fail(new InvalidOperationException(error));
            return;
        }
        progress = progress with
        {
            Status = update.Status ?? progress.Status,
            Fraction = update.Fraction ?? progress.Fraction,
            Summary = update.Summary ?? progress.Summary,
            ActivityPath = update.ActivityPath ?? progress.ActivityPath,
        };
        Write();
        if (!update.Running)
            EndTrack(id);
    }

    private void EndTrack(int id)
    {
        if (!tracks.TryGetValue(id, out var subscription)) return;
        tracks = tracks.Remove(id);
        subscription.Dispose();
        SettleIfDone();
    }

    private void ActionCompleted()
    {
        if (actionDone) return;
        actionDone = true;
        AnswerAll(refusal: null);
        SettleIfDone();
    }

    private void Fail(Exception exception)
    {
        if (settled) return;
        logger.LogWarning(exception,
            "Click action on area {Area} of {Hub} failed — refused to the client: {Message}",
            ProgressArea, host.Hub.Address, exception.Message);
        actionDone = true;
        AnswerAll(exception.Message);
        progress = progress with { Error = exception.Message };
        Settle();
    }

    private void Cancel()
    {
        if (settled || progress.Cancelling) return;
        // The cancel runs AS THE CLICKER: its handlers write (hub.CancelActivity patches the activity),
        // and a write needs a real identity on the context — the hub turn this runs on carries none.
        using var asClicker = host.ViewerAccess?.SwitchAccessContext(clicker);
        logger.LogInformation("Click on {Area} of {Hub} cancelled by the viewer", ProgressArea, host.Hub.Address);
        progress = progress with { Cancelling = true, Cancellable = false, Status = host.Localize("click.cancelling") };
        Write();
        cancellation.Cancel();
        foreach (var handler in cancelHandlers)
            try { handler(); }
            catch (Exception ex) { logger.LogWarning(ex, "Cancel handler for click on {Area} threw", ProgressArea); }

        if (!actionDone)
        {
            actionDone = true;
            actionSubscription.Dispose();
            AnswerAll(host.Localize("click.cancelled"));
        }
        // A tracked source that does not follow the cancel through (a generic observable) is disposed;
        // one that does (an activity, which reports its own Cancelled) keeps the button honest until it lands.
        foreach (var (id, subscription) in tracks.Where(t => !followThroughCancel.Contains(t.Key)).ToArray())
        {
            tracks = tracks.Remove(id);
            subscription.Dispose();
        }
        progress = progress with { Summary = progress.Summary ?? host.Localize("click.cancelled") };
        SettleIfDone();
    }

    private void SettleIfDone()
    {
        if (!settled && actionDone && tracks.IsEmpty)
            Settle();
    }

    private void Settle()
    {
        if (settled) return;
        settled = true;
        progress = progress with { Running = false, Cancellable = false, Cancelling = false, Status = null };
        Write();
        Dispose();
        onSettled(this);
    }

    private void AnswerAll(string? refusal)
    {
        if (receiptAnswered) return;
        receiptAnswered = true;
        receiptRefusal = refusal;
        foreach (var request in receipts)
            Answer(request, refusal);
        receipts = [];
    }

    private void Answer(IMessageDelivery<ClickedEvent> request, string? refusal)
    {
        if (refusal is null)
            host.Hub.Post(new UserActionAccepted(), o => o.ResponseFor(request));
        else
            host.Hub.Post(new DeliveryFailure(request, refusal), o => o.ResponseFor(request));
    }

    private void Write() => host.UpdateData(ClickProgress.DataId(ProgressArea), progress);

    private void OnHub(Action action) =>
        host.InvokeAsync(action, ex =>
        {
            logger.LogWarning(ex, "Click session step on {Area} of {Hub} threw", ProgressArea, host.Hub.Address);
            return Task.CompletedTask;
        });

    /// <summary>
    /// Releases the action, every tracked source and the cancel watch. Deliberately does NOT trip
    /// <see cref="Token"/>: settling (or the viewer leaving the page) is not a cancel, and work the
    /// click started — an approval's run — must not stop because nobody is watching its button.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        actionSubscription.Dispose();
        foreach (var subscription in tracks.Values)
            subscription.Dispose();
        tracks = tracks.Clear();
        subscriptions.Dispose();
        // The CancellationTokenSource is NOT disposed: the action may still hold (and register on) its
        // token after the click settled, and a timer-less source owns nothing that needs releasing.
    }

}
