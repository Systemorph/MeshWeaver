using System;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.Testing.FaultInjection;

/// <summary>
/// How a faulted delivery ends, from the SENDER's point of view. A webhook sender such as GitHub does
/// not retry either one, which is why both lose the event unless something else re-feeds it.
/// </summary>
public enum InboxFault
{
    /// <summary>The inbox never ran: the request died on a terminating pod and the sender saw an error status.</summary>
    Refused,

    /// <summary>The inbox ran and the sender still saw an error status (a reply lost in the roll).</summary>
    DeliveredButFailed,
}

/// <summary>One delivery a fault met: which fault, and what was being delivered.</summary>
/// <param name="Fault">How the delivery ended.</param>
/// <param name="What">A description of the delivery (the request line, or the message).</param>
public sealed record FaultedDelivery(InboxFault Fault, string What);

/// <summary>
/// The <b>webhook loss</b> injector for an inbox reached over HTTP: a <see cref="DelegatingHandler"/>
/// placed in front of the inbox (for example over a <c>TestServer</c> handler) that, while a switch
/// is closed, answers deliveries with an error status — either WITHOUT forwarding them
/// (<see cref="Refuse"/>, the pod-roll 500 that GitHub never retries) or AFTER the inbox handled them
/// (<see cref="FailAfterDelivery"/>). Every faulted delivery is reported on <see cref="Faulted"/>, so
/// a test can assert that what the sender lost was re-fed by some other path (a sweep).
///
/// <para>For an inbox that is a function rather than an HTTP endpoint, use
/// <see cref="FaultInjectingInbox{TMessage,TResult}"/>, which injects the same two faults.</para>
/// </summary>
public sealed class FaultInjectingHttpHandler : DelegatingHandler
{
    private readonly ReplaySubject<FaultedDelivery> _faulted = new();
    private (FaultSwitch Switch, HttpStatusCode Status)? _refuse;
    private (FaultSwitch Switch, HttpStatusCode Status)? _failAfter;
    private readonly object _arm = new();

    /// <summary>Creates the handler over <paramref name="inner"/>, the inbox's own handler.</summary>
    /// <param name="inner">The handler that reaches the inbox.</param>
    public FaultInjectingHttpHandler(HttpMessageHandler inner) : base(inner) { }

    /// <summary>Every delivery a fault met (replayed).</summary>
    public IObservable<FaultedDelivery> Faulted => _faulted.AsObservable();

    /// <summary>Until released, answer every delivery with <paramref name="status"/> and never forward it.</summary>
    /// <param name="status">What the sender sees; 500 by default.</param>
    public FaultSwitch Refuse(HttpStatusCode status = HttpStatusCode.InternalServerError)
        => Arm(ref _refuse, status, $"inbox refuses with {(int)status}");

    /// <summary>Until released, forward every delivery and then answer <paramref name="status"/> anyway.</summary>
    /// <param name="status">What the sender sees; 500 by default.</param>
    public FaultSwitch FailAfterDelivery(HttpStatusCode status = HttpStatusCode.InternalServerError)
        => Arm(ref _failAfter, status, $"inbox fails after delivery with {(int)status}");

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var what = $"{request.Method} {request.RequestUri}";
        if (Current(ref _refuse) is { } refuse)
        {
            refuse.Switch.NoteArrival(what);
            _faulted.OnNext(new FaultedDelivery(InboxFault.Refused, what));
            return new HttpResponseMessage(refuse.Status) { RequestMessage = request };
        }
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (Current(ref _failAfter) is { } failAfter)
        {
            failAfter.Switch.NoteArrival(what);
            _faulted.OnNext(new FaultedDelivery(InboxFault.DeliveredButFailed, what));
            response.Dispose();
            return new HttpResponseMessage(failAfter.Status) { RequestMessage = request };
        }
        return response;
    }

    /// <summary>Each switch carries its OWN status, so two faults in force never answer with each other's.</summary>
    private FaultSwitch Arm(ref (FaultSwitch Switch, HttpStatusCode Status)? slot, HttpStatusCode status, string name)
    {
        var fault = new FaultSwitch(name);
        (FaultSwitch Switch, HttpStatusCode Status)? previous;
        lock (_arm)
        {
            previous = slot;
            slot = (fault, status);
        }
        previous?.Switch.Release();
        return fault;
    }

    private (FaultSwitch Switch, HttpStatusCode Status)? Current(ref (FaultSwitch Switch, HttpStatusCode Status)? slot)
    {
        lock (_arm)
            return slot is { Switch.IsClosed: true } armed ? armed : null;
    }
}

/// <summary>
/// The <b>webhook loss</b> injector for an inbox that is a function: wraps
/// <c>deliver: message → IObservable&lt;result&gt;</c> so that, while a switch is closed, a delivery
/// faults with <see cref="InboxDeliveryFailedException"/> either before the inbox ran
/// (<see cref="Refuse"/>) or after it did (<see cref="FailAfterDelivery"/>). Cold, like every
/// delivery it wraps. A delivery is request/response: the inbox's FIRST answer is the delivery's
/// outcome, so an inbox that answers and stays open does not hold the sender.
/// </summary>
/// <typeparam name="TMessage">What is delivered.</typeparam>
/// <typeparam name="TResult">What the inbox answers.</typeparam>
public sealed class FaultInjectingInbox<TMessage, TResult>(Func<TMessage, IObservable<TResult>> deliver)
{
    private readonly ReplaySubject<FaultedDelivery> _faulted = new();
    private FaultSwitch? _refuse;
    private FaultSwitch? _failAfter;

    /// <summary>Every delivery a fault met (replayed).</summary>
    public IObservable<FaultedDelivery> Faulted => _faulted.AsObservable();

    /// <summary>Until released, fault every delivery without running the inbox.</summary>
    public FaultSwitch Refuse() => Arm(ref _refuse, "inbox refuses deliveries");

    /// <summary>Until released, run the inbox and then fault the delivery anyway.</summary>
    public FaultSwitch FailAfterDelivery() => Arm(ref _failAfter, "inbox fails after delivery");

    /// <summary>Delivers <paramref name="message"/> through whatever fault is in force.</summary>
    /// <param name="message">The message.</param>
    public IObservable<TResult> Deliver(TMessage message)
        => Observable.Defer(() =>
        {
            var what = message?.ToString() ?? typeof(TMessage).Name;
            if (Volatile.Read(ref _refuse) is { IsClosed: true } refuse)
            {
                refuse.NoteArrival(what);
                _faulted.OnNext(new FaultedDelivery(InboxFault.Refused, what));
                return Observable.Throw<TResult>(new InboxDeliveryFailedException(InboxFault.Refused, what));
            }
            if (Volatile.Read(ref _failAfter) is { IsClosed: true } failAfter)
                return deliver(message).Take(1).DefaultIfEmpty().SelectMany(_ =>
                {
                    failAfter.NoteArrival(what);
                    _faulted.OnNext(new FaultedDelivery(InboxFault.DeliveredButFailed, what));
                    return Observable.Throw<TResult>(new InboxDeliveryFailedException(InboxFault.DeliveredButFailed, what));
                });
            return deliver(message);
        });

    private static FaultSwitch Arm(ref FaultSwitch? slot, string name)
    {
        var fault = new FaultSwitch(name);
        Interlocked.Exchange(ref slot, fault)?.Release();
        return fault;
    }
}

/// <summary>The sender's view of a delivery a <see cref="FaultInjectingInbox{TMessage,TResult}"/> faulted.</summary>
/// <param name="fault">How it ended.</param>
/// <param name="what">What was delivered.</param>
public sealed class InboxDeliveryFailedException(InboxFault fault, string what)
    : Exception($"Delivery {fault}: {what} (injected)")
{
    /// <summary>How the delivery ended.</summary>
    public InboxFault Fault { get; } = fault;
}
