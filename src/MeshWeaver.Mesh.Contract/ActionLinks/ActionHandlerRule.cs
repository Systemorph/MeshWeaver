using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.ActionLinks;

/// <summary>
/// One entry in a portal's chain of action handlers — the content of an <c>ActionHandlerRule</c>
/// mesh node. A rule CLAIMS handler keys (its own identifiers, never the platform's set), says
/// which payload versions it reads, where it sits in the chain (<see cref="Order"/>), and which node
/// acts for it (<see cref="Handler"/>).
///
/// <para>🚨 <b>The chain is DURABLE, not a service registration</b> — policy
/// <c>open-vocabulary-string-constants</c>, Doc/Architecture/OpenVocabulariesAsStringConstants →
/// "The chain is DURABLE". A package the mesh compiles (a store) is not a compiled module and cannot
/// put a service into the root container; it CAN write a node. So a handler is contributed by writing
/// a rule node, the chain is read LIVE from the mesh (a rule added while the portal runs is picked
/// up on the next resolution, with no recycle), and the platform's own handlers are ordinary rules
/// with no earlier pass reserved for them.</para>
///
/// <para>Init-only properties rather than a positional record, deliberately: a new optional field is
/// then additive for every module compiled against an earlier build.</para>
/// </summary>
public sealed record ActionHandlerRule
{
    /// <summary>Identifies the rule node.</summary>
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The handler keys this rule claims (<see cref="ActionInvocation.HandlerKey"/>), compared
    /// ordinally — the rule author's OWN identifiers. A rule with no claims claims nothing.
    /// </summary>
    public ImmutableArray<string> Claims { get; init; } = [];

    /// <summary>The lowest <see cref="ActionInvocation.HandlerVersion"/> the rule's handler reads.</summary>
    public int MinVersion { get; init; } = 1;

    /// <summary>The highest <see cref="ActionInvocation.HandlerVersion"/> the rule's handler reads.</summary>
    public int MaxVersion { get; init; } = 1;

    /// <summary>
    /// Position in the chain — lower runs first; between two rules claiming the same key and
    /// version the lower order wins, and an EQUAL order is
    /// <see cref="ActionOutcomeStatus.AmbiguousHandler"/>. Explicit and durable — never registration,
    /// load or query order.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// The mesh path of the node that acts for this rule — the handler's own typed node, whose page
    /// is the review page a recipient is sent to (<see cref="IActionHandler.Prepare"/> answers
    /// <see cref="ActionOutcomeStatus.Ready"/> with a navigation to it). That page re-validates the
    /// domain from current state and is where the person confirms; whatever changes is changed there
    /// through <c>GetMeshNodeStream(path).Update(…)</c>.
    ///
    /// <para>🚨 It must lie in the rule's OWN partition (its first path segment): a rule routes a key
    /// only into the content of whoever wrote it. A rule that names a node elsewhere, or no node, is
    /// refused at binding and reported (<see cref="ActionHandlerRules.Bind"/>) — never silently
    /// skipped.</para>
    /// </summary>
    public string Handler { get; init; } = string.Empty;

    /// <summary>Whether completing the action changes anything (<see cref="ActionHandlerCapability.Mutates"/>).</summary>
    public bool Mutates { get; init; }

    /// <summary>
    /// The capabilities the rule's handler offers on this portal
    /// (<see cref="ActionHandlerCapability.Capabilities"/>) — <see cref="ActionCapabilities"/> words
    /// or the author's own.
    /// </summary>
    public ImmutableArray<string> Capabilities { get; init; } = [];

    /// <summary>
    /// The caption of the navigation to <see cref="Handler"/>, as the rule's author wrote it —
    /// authored content, rendered as authored. Empty falls back to the handler path, an identifier.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The author's own catalog key that renders <see cref="Label"/> in the viewer's language, or null.</summary>
    public string? LabelKey { get; init; }

    /// <summary>A disabled rule claims nothing, and a deployment may disable one without a build.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// WHO may publish an invocation this rule handles: the name of the <see cref="Permission"/>
    /// (<c>Update</c>, <c>Create</c>, …) the PUBLISHER must hold on <see cref="Handler"/>, checked
    /// through the mesh's permission seam when a link is validated for publication
    /// (<see cref="IActionHandler.Validate"/>).
    ///
    /// <para>🚨 <b>Fails closed.</b> A rule that states no permission, or a word that is not one
    /// <see cref="Permission"/> flag, authorizes NO publisher: the right to distribute an action is
    /// stated by the rule's author, never assumed.</para>
    /// </summary>
    public string? PublishPermission { get; init; }

    /// <summary>
    /// WHERE <see cref="PublishPermission"/> is checked: a node path, or null to check it on
    /// <see cref="Handler"/>. 🚨 A handler page shipped in a package lives in a partition the package's
    /// one-way sync owns, and on such a partition nobody can be granted a write right — so a rule that
    /// asks for <c>Update</c> there authorizes no publisher at all. Naming a node in a WRITABLE space
    /// (for example a publishers node an administrator grants a group <c>Editor</c> on) is how such a
    /// rule gets publishers. The path is only CHECKED, never navigated, so it may lie outside the rule's
    /// partition; when set it must be a canonical mesh path — a blank one is refused too, never read as
    /// "absent" (<see cref="ActionHandlerRules.UnsafePublishScope"/>).
    /// A rule that names a scope nobody holds the permission on still authorizes nobody — it fails closed.
    /// </summary>
    public string? PublishScope { get; init; }
}

/// <summary>
/// A rule node the chain could not use, and why — reported, never dropped in silence.
/// </summary>
/// <param name="Path">The rule node's path.</param>
/// <param name="Reason">An identifier for operators and logs: <c>UndeclaredNamespace</c>, <c>UnreadableContent</c>, <c>NoHandler</c>, <c>HandlerOutsidePartition</c>, <c>UnsafeHandler</c>, <c>UnsafePublishScope</c>.</param>
public sealed record RejectedActionHandlerRule(string Path, string Reason);

/// <summary>
/// The chain as one snapshot of the mesh's rule nodes: the handlers it binds, in no implied order
/// (<see cref="ActionHandlerCapability.Order"/> decides), and the rules it refused.
/// </summary>
public sealed record ActionHandlerChain
{
    /// <summary>The handlers bound from enabled, well-formed rules — one per claimed key.</summary>
    public ImmutableArray<IActionHandler> Handlers { get; init; } = [];

    /// <summary>How many rule nodes the snapshot held, enabled or not.</summary>
    public int RulesRead { get; init; }

    /// <summary>The rules that could not be bound, each with its reason.</summary>
    public ImmutableArray<RejectedActionHandlerRule> Rejected { get; init; } = [];

    /// <summary>
    /// The namespaces whose rules counted for this snapshot — the platform's declared ones and each
    /// installed package's own (<see cref="ActionHandlerRules.Bind"/>). A rule outside them is in
    /// <see cref="Rejected"/> as <see cref="ActionHandlerRules.UndeclaredNamespace"/>.
    /// </summary>
    public ImmutableSortedSet<string> DeclaredNamespaces { get; init; } =
        ImmutableSortedSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves <paramref name="invocation"/> over <see cref="Handlers"/>
    /// (<see cref="ActionHandlerResolution.Resolve"/>). An UNCLAIMED key is the chain's loud
    /// terminal: the <see cref="ActionOutcomeStatus.UnknownHandler"/> outcome's
    /// <see cref="ActionOutcome.Detail"/> says how many rules were read and names every rule that was
    /// refused, so "nothing claims it" and "the rule that would have claimed it is broken" read
    /// differently. Pure.
    /// </summary>
    /// <param name="invocation">The invocation to resolve.</param>
    /// <returns>The resolution.</returns>
    public ActionHandlerResolution Resolve(ActionInvocation invocation)
    {
        var resolution = ActionHandlerResolution.Resolve(Handlers, invocation);
        if (resolution.Outcome.Status != ActionOutcomeStatus.UnknownHandler)
            return resolution;
        var refused = Rejected.IsDefaultOrEmpty
            ? string.Empty
            : "; refused: " + string.Join(", ", Rejected.Select(r => $"{r.Path} ({r.Reason})"));
        var declared = DeclaredNamespaces.Count == 0 ? "none" : string.Join(", ", DeclaredNamespaces);
        return resolution with
        {
            Outcome = resolution.Outcome with
            {
                Detail = $"no enabled rule claims it ({RulesRead} rule(s) read; declared namespaces: {declared}{refused})",
            },
        };
    }
}

/// <summary>
/// Binds rule nodes into the handler chain. Pure — the live mesh read lives beside the node type
/// (<c>ActionHandlerRuleExtensions.ActionHandlerChain</c> in <c>MeshWeaver.Graph</c>), so the same
/// verdict comes from a test's list of rules and from a running portal.
/// </summary>
public static class ActionHandlerRules
{
    /// <summary>
    /// The rule lies outside every DECLARED namespace — neither the platform's own nor an installed
    /// package's. Writing a node somewhere is not a declaration: without this, anyone with Create in
    /// some partition could add a lower-<see cref="ActionHandlerRule.Order"/> rule and route a key
    /// to their own page.
    /// </summary>
    public const string UndeclaredNamespace = "UndeclaredNamespace";

    /// <summary>The handler path does not resolve to a node in the rule's own partition.</summary>
    public const string HandlerOutsidePartition = "HandlerOutsidePartition";

    /// <summary>The rule names no handler node.</summary>
    public const string NoHandler = "NoHandler";

    /// <summary>
    /// The handler path would not make a local target, or is not a canonical mesh path — a dot
    /// segment (<c>Store/../Admin</c>), a percent-escape (<c>Store/%2e%2e/Admin</c>), a query or
    /// fragment delimiter, a backslash or an empty segment. The browser normalises those AFTER the
    /// own-partition check, so any of them could leave the rule's partition.
    /// </summary>
    public const string UnsafeHandler = "UnsafeHandler";

    /// <summary>The rule's <see cref="ActionHandlerRule.PublishScope"/> is not a canonical mesh path.</summary>
    public const string UnsafePublishScope = "UnsafePublishScope";

    /// <summary>The rule node's content does not read as an <see cref="ActionHandlerRule"/>.</summary>
    public const string UnreadableContent = "UnreadableContent";

    /// <summary>
    /// Binds every enabled, well-formed rule in a DECLARED namespace into one handler per claimed
    /// key, and reports every rule that cannot be bound — a rule outside
    /// <paramref name="declaredNamespaces"/> is refused as <see cref="UndeclaredNamespace"/>, never
    /// silently dropped. A disabled rule is neither bound nor refused.
    /// </summary>
    /// <param name="rules">Each rule node's path and its content, or null when the content did not read.</param>
    /// <param name="declaredNamespaces">The namespaces (first path segments, compared ignoring case)
    /// whose rules count: the platform's declared ones and each installed package's own.</param>
    /// <param name="authorizePublisher">The permission seam a bound handler asks whether a publisher
    /// holds a rule's <see cref="ActionHandlerRule.PublishPermission"/> on its handler node — on a
    /// live mesh, <c>hub.CheckPermissionOutcome</c>. Null (a pure binding) authorizes nobody: every
    /// publication then validates as <see cref="ActionOutcomeStatus.Unauthorized"/>.</param>
    /// <returns>The chain.</returns>
    public static ActionHandlerChain Bind(
        IEnumerable<(string Path, ActionHandlerRule? Rule)> rules,
        IEnumerable<string> declaredNamespaces,
        Func<string, string, Permission, IObservable<PermissionCheckOutcome>>? authorizePublisher = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(declaredNamespaces);
        var declared = declaredNamespaces
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Select(ns => ns.Trim().Trim('/'))
            .ToImmutableSortedSet(StringComparer.OrdinalIgnoreCase);
        var handlers = ImmutableArray.CreateBuilder<IActionHandler>();
        var rejected = ImmutableArray.CreateBuilder<RejectedActionHandlerRule>();
        var read = 0;
        foreach (var (path, rule) in rules)
        {
            read++;
            if (rule is null)
            {
                rejected.Add(new(path, UnreadableContent));
                continue;
            }
            if (!rule.Enabled)
                continue;
            if (!declared.Contains(Partition(path)))
            {
                rejected.Add(new(path, UndeclaredNamespace));
                continue;
            }
            var refusal = Refusal(path, rule);
            if (refusal is not null)
            {
                rejected.Add(new(path, refusal));
                continue;
            }
            foreach (var key in (rule.Claims.IsDefault ? [] : rule.Claims)
                         .Where(k => !string.IsNullOrWhiteSpace(k))
                         .Distinct(StringComparer.Ordinal))
                handlers.Add(new RuleBoundActionHandler(path, rule, key, authorizePublisher));
        }
        return new ActionHandlerChain
        {
            Handlers = handlers.ToImmutable(),
            RulesRead = read,
            Rejected = rejected.ToImmutable(),
            DeclaredNamespaces = declared,
        };
    }

    /// <summary>The navigation target for a rule's handler node, carrying the link followed.</summary>
    /// <param name="handler">The handler node's path.</param>
    /// <param name="linkId">The link the recipient followed, or null.</param>
    /// <returns>A path on this site.</returns>
    public static string Target(string handler, string? linkId)
    {
        var target = "/" + handler.Trim().Trim('/');
        return string.IsNullOrEmpty(linkId) ? target : $"{target}?link={Uri.EscapeDataString(linkId)}";
    }

    private static string? Refusal(string path, ActionHandlerRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Handler))
            return NoHandler;
        if (!IsCanonicalMeshPath(rule.Handler)
            || !ActivityActionReference.IsLocalTarget(Target(rule.Handler, null)))
            return UnsafeHandler;
        if (rule.PublishScope is { } scope && !IsCanonicalMeshPath(scope))
            return UnsafePublishScope;
        // Partitions are case-insensitive everywhere (DeclaredNamespaces, schema routing).
        return string.Equals(Partition(path), Partition(rule.Handler), StringComparison.OrdinalIgnoreCase)
            ? null
            : HandlerOutsidePartition;
    }

    /// <summary>
    /// Whether <paramref name="handler"/> is already the path the browser will request: no dot
    /// segment, no empty segment, and none of the characters a browser or server decodes or splits
    /// on (<c>%</c>, <c>?</c>, <c>#</c>, <c>\</c>). Only such a path can have its partition judged
    /// from its first segment.
    /// </summary>
    private static bool IsCanonicalMeshPath(string handler)
    {
        var trimmed = handler.Trim().Trim('/');
        if (trimmed.Length == 0 || trimmed.IndexOfAny(['%', '?', '#', '\\']) >= 0)
            return false;
        return trimmed.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    private static string Partition(string path)
    {
        var trimmed = path.Trim().Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    /// <summary>
    /// The single <see cref="Permission"/> flag <paramref name="word"/> names, or null when it names
    /// none, several, or <see cref="Permission.None"/>. Pure.
    /// </summary>
    private static Permission? ParsePermission(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)
            || !Enum.TryParse<Permission>(word.Trim(), ignoreCase: true, out var permission)
            || permission == Permission.None
            || !Enum.IsDefined(permission)
            || int.TryParse(word.Trim(), out _))
            return null;
        return permission;
    }

    /// <summary>
    /// A handler declared by a rule node. Validate is the structural check followed by the rule's
    /// publisher authorization (<see cref="ActionHandlerRule.PublishPermission"/>, failing closed);
    /// Prepare sends the recipient to the rule's handler node, whose page re-validates the domain
    /// and is where the person confirms. Read-only — it touches nothing.
    /// </summary>
    private sealed class RuleBoundActionHandler(
        string rulePath,
        ActionHandlerRule rule,
        string key,
        Func<string, string, Permission, IObservable<PermissionCheckOutcome>>? authorizePublisher) : IActionHandler
    {
        public ActionHandlerCapability Capability { get; } = new()
        {
            HandlerKey = key,
            MinVersion = rule.MinVersion,
            MaxVersion = rule.MaxVersion,
            Order = rule.Order,
            Mutates = rule.Mutates,
            Capabilities = rule.Capabilities.IsDefault ? [] : rule.Capabilities,
        };

        public IObservable<ActionOutcome> Validate(ActionInvocation invocation, ActionPublicationContext context) =>
            Observable.Defer(() =>
            {
                ArgumentNullException.ThrowIfNull(context);
                var structural = Capability.Supports(invocation);
                if (structural.Status != ActionOutcomeStatus.Ready)
                    return Observable.Return(structural);
                if (ParsePermission(rule.PublishPermission) is not { } permission)
                    return Observable.Return(ActionOutcome.Of(ActionOutcomeStatus.Unauthorized, invocation,
                        string.IsNullOrWhiteSpace(rule.PublishPermission)
                            ? $"rule {rulePath} states no publish permission, so no publisher may distribute it"
                            : $"rule {rulePath} names publish permission '{rule.PublishPermission}', which is not one permission"));
                var publisher = context.Publisher?.Trim();
                if (string.IsNullOrEmpty(publisher) || RequestIdentity.IsReservedPrincipal(publisher))
                    return Observable.Return(ActionOutcome.Of(ActionOutcomeStatus.Unauthorized, invocation,
                        "the publisher is not an authenticated person or service"));
                if (authorizePublisher is null)
                    return Observable.Return(ActionOutcome.Of(ActionOutcomeStatus.Unauthorized, invocation,
                        $"rule {rulePath}: no permission seam is bound to this chain, so no publisher is authorized"));
                // The publish check's node: the rule's PublishScope when it names one, else its handler.
                var handlerPath = (rule.PublishScope ?? rule.Handler).Trim().Trim('/');
                return authorizePublisher(handlerPath, publisher, permission)
                    .Take(1)
                    .DefaultIfEmpty(PermissionCheckOutcome.Undetermined("the permission check produced no verdict"))
                    .Select(check => check.IsGranted
                        ? structural
                        : check.IsUndetermined
                            ? ActionOutcome.Of(ActionOutcomeStatus.Failed, invocation,
                                $"publish permission {permission} on {handlerPath} could not be checked: {check.UndeterminedReason}")
                            : ActionOutcome.Of(ActionOutcomeStatus.Unauthorized, invocation,
                                $"the publisher lacks {permission} on {handlerPath} (rule {rulePath})"));
            });

        public IObservable<ActionOutcome> Prepare(ActionInvocation invocation, ActionRecipientContext context) =>
            Observable.Defer(() =>
            {
                var target = Target(rule.Handler, context.LinkId);
                if (!ActivityActionReference.IsLocalTarget(target))
                    return Observable.Return(ActionOutcome.Of(ActionOutcomeStatus.Failed, invocation,
                        $"rule {rulePath}: the link id does not make a local target"));
                return Observable.Return(ActionOutcome.Of(ActionOutcomeStatus.Ready, invocation) with
                {
                    Navigation = new ActivityActionReference
                    {
                        Id = "review",
                        Label = string.IsNullOrWhiteSpace(rule.Label) ? rule.Handler.Trim().Trim('/') : rule.Label,
                        LabelKey = rule.LabelKey,
                        Target = target,
                        HandlerKey = invocation.HandlerKey,
                        HandlerVersion = invocation.HandlerVersion,
                    },
                });
            });

        public override string ToString() => $"{rulePath} → {key}";
    }
}
