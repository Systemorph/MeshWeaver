using System.Collections.Immutable;

namespace MeshWeaver.ActionLinks;

/// <summary>
/// What one registered <see cref="IActionHandler"/> declares about itself: the key it answers to,
/// the payload versions it reads, whether it changes anything, and the capabilities it can offer on
/// this portal right now.
///
/// <para>Registration is EXPLICIT by key and version, and DURABLE: on a portal every handler comes
/// from an <see cref="ActionHandlerRule"/> node in the mesh (<see cref="ActionHandlerRules.Bind"/>),
/// never from a service a compiled module registers. A publisher asks before it publishes
/// (<see cref="Supports"/>), so an advertised link never depends on a module that merely happens to
/// be loaded, and a recipient who follows an old link gets a named outcome rather than a blank
/// page.</para>
///
/// <para>Init-only properties rather than a positional record, deliberately: a new optional field is
/// then additive for every module compiled against an earlier build.</para>
/// </summary>
public sealed record ActionHandlerCapability
{
    /// <summary>The key this handler answers to; compared ordinally with <see cref="ActionInvocation.HandlerKey"/>.</summary>
    public required string HandlerKey { get; init; }

    /// <summary>The lowest <see cref="ActionInvocation.HandlerVersion"/> this handler still reads.</summary>
    public int MinVersion { get; init; } = 1;

    /// <summary>The highest <see cref="ActionInvocation.HandlerVersion"/> this handler reads.</summary>
    public int MaxVersion { get; init; } = 1;

    /// <summary>
    /// The handler's position in the chain — lower runs first. When several handlers read the same
    /// key and version, the one with the LOWEST order is chosen; two sharing that lowest order is
    /// <see cref="ActionOutcomeStatus.AmbiguousHandler"/>. Explicit and DURABLE (it is the
    /// <see cref="ActionHandlerRule.Order"/> of the rule node the handler came from), never
    /// registration or load order (policy <c>open-vocabulary-string-constants</c>).
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether completing the action changes anything (grants access, saves a payment method).
    ///
    /// <para>🚨 A mutating handler's action is never completed by opening a link. The publisher's
    /// page shows what will happen and the person confirms it; a GET, a crawler preview, a scan and
    /// a sign-in callback only ever reach <see cref="IActionHandler.Prepare"/>, which is
    /// read-only.</para>
    /// </summary>
    public bool Mutates { get; init; }

    /// <summary>
    /// The capabilities this handler can offer on THIS portal now — <see cref="ActionCapabilities"/>
    /// words or a module's own. Computed by the handler from the deployment's actual state (a
    /// payment provider that supports setup is mounted), never a static claim.
    /// </summary>
    public ImmutableArray<string> Capabilities { get; init; } = [];

    /// <summary>
    /// Whether this handler supports <paramref name="invocation"/>, as an outcome naming the first
    /// thing that does not fit: <see cref="ActionOutcomeStatus.UnsupportedContract"/>,
    /// <see cref="ActionOutcomeStatus.UnknownHandler"/> (the key is not this handler's),
    /// <see cref="ActionOutcomeStatus.UnsupportedVersion"/>,
    /// <see cref="ActionOutcomeStatus.MissingCapability"/> — or
    /// <see cref="ActionOutcomeStatus.Ready"/>. Pure.
    /// </summary>
    /// <param name="invocation">The invocation to check.</param>
    /// <returns>The outcome; <see cref="ActionOutcome.Detail"/> carries the supported range or the missing words.</returns>
    public ActionOutcome Supports(ActionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.ContractVersion < 1 || invocation.ContractVersion > ActionInvocation.CurrentContractVersion)
            return ActionOutcome.Of(ActionOutcomeStatus.UnsupportedContract, invocation,
                $"contract {invocation.ContractVersion}; this build reads 1..{ActionInvocation.CurrentContractVersion}");
        if (!string.Equals(HandlerKey, invocation.HandlerKey, StringComparison.Ordinal))
            return ActionOutcome.Of(ActionOutcomeStatus.UnknownHandler, invocation);
        if (invocation.HandlerVersion < MinVersion || invocation.HandlerVersion > MaxVersion)
            return ActionOutcome.Of(ActionOutcomeStatus.UnsupportedVersion, invocation,
                $"supported {MinVersion}..{MaxVersion}");

        var offered = Capabilities.IsDefault ? [] : Capabilities;
        var required = invocation.RequiredCapabilities.IsDefault ? [] : invocation.RequiredCapabilities;
        var missing = required
            .Where(word => !offered.Contains(word, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return missing.Length > 0
            ? ActionOutcome.Of(ActionOutcomeStatus.MissingCapability, invocation, string.Join(", ", missing))
            : ActionOutcome.Of(ActionOutcomeStatus.Ready, invocation);
    }
}
