namespace MeshWeaver.Payments;

/// <summary>
/// What a payment provider must be TOLD to open a hosted billing portal for one subscriber — who
/// they are at the processor, and where to send them back to.
///
/// <para>At least one of <see cref="CustomerId"/> and <see cref="SubscriptionId"/> must be set. The
/// customer is the processor's own handle and the direct answer; the subscription is the fallback
/// every card-sold plan already carries, which the provider resolves to its customer.</para>
/// </summary>
public sealed record PaymentBillingPortalRequest
{
    /// <summary>The processor's customer handle, when the caller recorded it.</summary>
    public string? CustomerId { get; init; }

    /// <summary>The processor's subscription handle — resolved to its customer when
    /// <see cref="CustomerId"/> is absent.</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>Where the processor returns the subscriber when they leave the portal. The
    /// CALLER's policy, never the provider's — the same rule as a checkout's return URLs.</summary>
    public required string ReturnUrl { get; init; }

    /// <summary>Whether the request names a subscriber at all. Pure.</summary>
    public bool NamesSubscriber =>
        !string.IsNullOrWhiteSpace(CustomerId) || !string.IsNullOrWhiteSpace(SubscriptionId);
}

/// <summary>One opened hosted billing-portal session.</summary>
/// <param name="Url">Where to send the subscriber. Short-lived — open it now, never store it.</param>
public sealed record PaymentBillingPortal(string Url);
