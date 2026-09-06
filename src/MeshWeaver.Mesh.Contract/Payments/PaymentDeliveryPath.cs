namespace MeshWeaver.Payments;

/// <summary>
/// The provider's account of the DELIVERY half of the payment path — the half that lives at the
/// processor, where a portal cannot see it.
///
/// <para>🚨 <b>A read that FAILED is not an account with no endpoints.</b> An unreachable provider,
/// a refused credential, a truncated listing and an unparseable body all produce "nothing found",
/// and collapsing them into "there is no endpoint" is exactly the conflation that left a portal
/// silently taking money and never fulfilling anything (MeshWeaver.Plugins#1109). So
/// <see cref="ReadProblem"/> and <see cref="Truncated"/> are carried separately, and a caller that
/// cannot tell must say so rather than report health.</para>
/// </summary>
public sealed record PaymentDeliveryPath
{
    /// <summary>
    /// The account MODE this portal's credential operates in, as operator-facing display text
    /// ("TEST", "LIVE", or the provider's word for "not one I recognise").
    ///
    /// <para>Named in every line because it is the fact the whole question turns on: a provider
    /// that keeps separate test and live configurations lists only the endpoints of the mode its
    /// credential belongs to, so "no endpoint for my URL" means "no endpoint IN THIS MODE" — which
    /// is what tells "never registered" from "registered in the other one".</para>
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>The endpoints the provider reports for this account, in this mode.</summary>
    public IReadOnlyList<PaymentDeliveryEndpoint> Endpoints { get; init; } = [];

    /// <summary>
    /// Whether the listing was TRUNCATED. A partial listing cannot prove an endpoint absent — "I
    /// did not see it on the first page" is not "it does not exist".
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Why the listing could not be read, or null when it was read. Carries the failure KIND and
    /// nothing that could restate a credential.
    /// </summary>
    public string? ReadProblem { get; init; }

    /// <summary>
    /// The delivery events this provider's fulfilment path needs an endpoint to be subscribed to.
    /// The provider names them because they are its own event vocabulary.
    /// </summary>
    public IReadOnlyList<string> RequiredEvents { get; init; } = [];

    /// <summary>
    /// What an operator DOES about a broken delivery path, in the provider's own terms — carried in
    /// the report because a diagnosis without its remedy is what left the defect open for days.
    /// </summary>
    public required string Remedy { get; init; }
}

/// <summary>One delivery endpoint as the provider reports it.</summary>
/// <param name="Url">The URL the provider posts deliveries to, verbatim.</param>
/// <param name="Enabled">Whether the provider would actually deliver to it. A disabled endpoint
/// delivers nothing, which is indistinguishable from an absent one on the receiving side.</param>
/// <param name="Status">The provider's own status word, for display; null when it named none.</param>
/// <param name="Events">The events it is subscribed to. <see cref="AllEvents"/> means every event.</param>
public sealed record PaymentDeliveryEndpoint(
    string Url,
    bool Enabled,
    string? Status,
    IReadOnlyList<string> Events)
{
    /// <summary>The wildcard subscription — an endpoint carrying it receives every event.</summary>
    public const string AllEvents = "*";

    /// <summary>
    /// Whether this endpoint's subscription covers <paramref name="wanted"/> — directly or through
    /// <see cref="AllEvents"/>. Pure.
    /// </summary>
    public bool Covers(string wanted) =>
        Events.Any(subscribed =>
            string.Equals(subscribed?.Trim(), AllEvents, StringComparison.Ordinal)
            || string.Equals(subscribed?.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
}
