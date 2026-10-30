using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MeshWeaver.Compiler;

/// <summary>One reference in in-mesh source to an API that lets code act as someone else.</summary>
/// <param name="Symbol">The member, <c>Namespace.Type.Member</c> — or the literal, quoted.</param>
/// <param name="Location">Where: the tree's path (empty for in-memory source) and the 1-based line.</param>
public sealed record InMeshImpersonationReference(string Symbol, string Location)
{
    /// <inheritdoc />
    public override string ToString() => $"{Symbol} at {Location}";
}

/// <summary>
/// 🚨 Option C of Doc/Architecture/InMeshImpersonation — the COMPILE-TIME half of the in-mesh
/// impersonation guard. The runtime guard judges the calling stack frame, which the JIT can erase
/// (a tail call) and a platform subscriber can replace (a delegate handed back), and it cannot see a
/// hand-built context passed to an API that is not a surface. The compiler sees every SYMBOL
/// REFERENCE in the source, however the call is later emitted — so a method group passed to Rx, a
/// call in tail position and a literal <c>"system-security"</c> are all found here.
///
/// <para><b>What is found</b> (<see cref="Members"/>): every impersonation surface; every API that
/// installs an identity without going through one (<c>IMessageDelivery.SetAccessContext</c>,
/// <c>IMessageHub.DeliverMessage</c>, <c>MeshQueryRequest.AsSystem</c> / <c>ForViewer</c> and a WRITE
/// to <c>MeshQueryRequest.UserId</c>, a <c>MeshQueryRequest.FromQuery</c> / <c>FromQueries</c> call
/// that SUPPLIES its <c>userId</c> parameter, a write to <c>AccessContext.IsHub</c>); every API that
/// mints or routes a delivery envelope without <c>hub.Post</c> stamping its sender (a
/// <c>MessageDelivery</c> constructor called with arguments, a write to <c>MessageDelivery.Sender</c>,
/// <c>IRoutingService.DeliverMessage</c> and the Orleans grain entry points); the System
/// identity's names (<c>WellKnownUsers.System</c> / <c>SystemContext</c>,
/// <c>AccessService.SystemObjectId</c>) and the literal itself — wherever it stands, a comparison
/// included: a syntactic scan cannot follow a value from a local into an identity, so it reports
/// the name and leaves the judgement to the trust list.</para>
///
/// <para><c>SwitchAccessContext</c> is deliberately NOT here: in-mesh code switches back to a
/// captured viewer legitimately. Switching to a System context that is NAMED in the source needs
/// one of the names above and is found through it; a System context that arrives as DATA (a
/// parameter, a captured delivery's context) names nothing and is NOT found here — the runtime
/// guard judges that call, within its own limits (Doc/Architecture/InMeshImpersonation → "What the
/// compile-time check cannot see").</para>
///
/// <para>Reflection by NAME is not a symbol reference and is not found; that residual is named in
/// the doc. Pure over the compilation; reads only the trees it is given — for a script submission
/// that is the submission's own tree, never the earlier cells' (they are a referenced previous
/// compilation, not syntax trees of this one).</para>
/// </summary>
public static class InMeshImpersonationReferences
{
    /// <summary>The System identity's id, as a literal in source.</summary>
    public const string SystemLiteral = "system-security";

    // Every member of IMailCopyStore / IMailAttachmentStore: each runs as System on the owner's behalf.
    private static readonly string[] MailCopyStoreMembers =
        ["Source", "Create", "Commit", "Disable", "Upsert", "WriteIfNewer", "ReplaceIfNewer", "Revision",
         "WriteIfUnchanged", "SetAttachments", "Remove", "RemoveGeneration", "List", "Read", "ReadPage", "SourceKeys", "EnabledSources", "RetireLegacy", "LegacySource"];

    private static readonly string[] MailAttachmentStoreMembers = ["Save", "DeleteAll", "DeleteLegacy"];

    /// <summary>
    /// Members whose mere reference is reported, keyed by <c>Namespace.TypeName</c> (no arity).
    /// Immutable constant lookup.
    /// </summary>
    public static readonly ImmutableDictionary<string, ImmutableHashSet<string>> Members =
        new Dictionary<string, string[]>
        {
            ["MeshWeaver.Messaging.AccessService"] =
                ["ImpersonateAsSystem", "ImpersonateAsSystemFor", "ImpersonateAsHub", "SetContext",
                 "SetCircuitContext", "SetHostIdentity", "SetStandingIdentity", "SystemObjectId"],
            ["MeshWeaver.Messaging.ImpersonationScopeExtensions"] = ["RunAsSystem", "RunAsSystemFor", "RunAsHub"],
            ["MeshWeaver.Messaging.PostOptions"] = ["ImpersonateAsHub"],
            ["MeshWeaver.Messaging.IMessageDelivery"] = ["SetAccessContext"],
            ["MeshWeaver.Messaging.MessageDelivery"] = ["SetAccessContext"],
            ["MeshWeaver.Messaging.IMessageHub"] = ["DeliverMessage"],
            ["MeshWeaver.Messaging.MessageHub"] = ["DeliverMessage"],
            // The routing entry points a hand-built envelope reaches without hub.Post, which is the
            // only path that stamps the sender (Doc/Architecture/SenderIsThePostingHub, #6619).
            ["MeshWeaver.Mesh.Services.IRoutingService"] = ["DeliverMessage"],
            ["MeshWeaver.Connection.Orleans.OrleansRoutingService"] = ["DeliverMessage"],
            ["MeshWeaver.Connection.Orleans.IMessageHubGrain"] = ["DeliverMessage"],
            ["MeshWeaver.Connection.Orleans.IRoutingGrain"] = ["RouteMessage"],
            ["MeshWeaver.Connection.Orleans.IPodHubGrain"] = ["Deliver"],
            // The public concrete stream forwards a caller-built envelope straight to its hub, keeping
            // the sender it names (the interface member is internal; the class's is public).
            ["MeshWeaver.Data.Serialization.SynchronizationStream"] = ["DeliverMessage"],
            ["MeshWeaver.Mesh.Security.AccessContextScope"] = ["AsSystem"],
            ["MeshWeaver.Mesh.Security.WellKnownUsers"] = ["System", "SystemContext"],
            ["MeshWeaver.Mesh.Services.MeshQueryRequest"] = ["AsSystem", "ForViewer"],
            ["MeshWeaver.ContentCollections.ContentImportBuilder"] = ["ImpersonateAsSystem"],
            ["MeshWeaver.ContentCollections.SyncContentFilesBuilder"] = ["ImpersonateAsSystem"],
            // The storage repair sweeps write as System over any partition the caller names (Doc/Architecture/
            // MarkdownBodyKeyRepair, StaleMainNodeRepair): an operator's one-shot, never in-mesh code's.
            ["MeshWeaver.Hosting.Persistence.MarkdownBodyKeyRepair"] = ["Repair"],
            ["MeshWeaver.Hosting.Persistence.StaleMainNodeRepair"] = ["Repair"],
            // The mail copy's stores write and read as System on the owner's behalf. Their runtime check judges
            // the immediate caller, which a method group handed to a platform callback replaces — so both the
            // interfaces and the concrete stores are reported here, and every public road to Enable (it starts the
            // System backfill): the engine, the per-account copy and the switch over the providers.
            // 🚨 These types are NOT core's: the mail copy lives in the Mail MODULE (MeshWeaver.Plugins,
            // MeshWeaver.Mail.MicrosoftGraph, namespace MeshWeaver.Mail.MailCopy; design page Mail/MailCopy). The
            // table is keyed Namespace.TypeName with no assembly, so these names reach the module's types in any
            // in-mesh compile that references the module, and name nothing where it is not installed. A recorded
            // core→plugins edge by name (Doc/Architecture/CarvingProjectsOutOfCore → MailCopy); the module's
            // MailCopySurfaceRegistrationTest compares this list with its [ImpersonationSurface]s.
            ["MeshWeaver.Mail.MailCopy.IMailCopyStore"] = MailCopyStoreMembers,
            ["MeshWeaver.Mail.MailCopy.MeshMailCopyStore"] = MailCopyStoreMembers,
            ["MeshWeaver.Mail.MailCopy.IMailAttachmentStore"] = MailAttachmentStoreMembers,
            ["MeshWeaver.Mail.MailCopy.MeshMailAttachmentStore"] = MailAttachmentStoreMembers,
            ["MeshWeaver.Mail.MailCopy.MailCopyEngine"] = ["Kick", "Sync", "Enable", "ServesFromCopy", "FollowMove"],
            ["MeshWeaver.Mail.MailCopy.IMailAccountCopy"] = ["KickAll", "Enable"],
            ["MeshWeaver.Mail.MailCopy.MailFeedAccountCopy"] = ["KickAll", "Enable"],
            ["MeshWeaver.Mail.MailCopy.IMailCopySwitch"] = ["Enable"],
            ["MeshWeaver.Mail.MailCopy.MailCopySwitch"] = ["Enable"],
        }.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Members reported only when WRITTEN (an assignment, an object or <c>with</c> initializer).</summary>
    public static readonly ImmutableDictionary<string, ImmutableHashSet<string>> WrittenMembers =
        new Dictionary<string, string[]>
        {
            ["MeshWeaver.Mesh.Services.MeshQueryRequest"] = ["UserId"],
            ["MeshWeaver.Messaging.AccessContext"] = ["IsHub"],
            // A delivery's sender rewritten on an envelope (`d with { Sender = … }`, #6619).
            ["MeshWeaver.Messaging.MessageDelivery"] = ["Sender"],
        }.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// Types whose constructors are reported when called WITH arguments, keyed by
    /// <c>Namespace.TypeName</c> (no arity). A delivery envelope built in-mesh names its own sender
    /// (directly, or through a caller-written <c>PostOptions.Sender</c>), and only <c>hub.Post</c>
    /// stamps the sender (#6619). The parameterless constructor carries no sender and is not
    /// reported; a sender written onto it afterwards is (<see cref="WrittenMembers"/>).
    /// Immutable constant lookup.
    /// </summary>
    public static readonly ImmutableHashSet<string> Constructors =
        ImmutableHashSet.Create(StringComparer.Ordinal, "MeshWeaver.Messaging.MessageDelivery");

    // The simple type names of Constructors: the prefilter for an explicit `new T(…)` (plus every
    // using-alias name of the compilation, see Find).
    private static readonly ImmutableHashSet<string> ConstructorTypeNames =
        Constructors.Select(t => t[(t.LastIndexOf('.') + 1)..]).ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Methods reported only when the reference SUPPLIES an argument for the named parameter — the
    /// factories that install a query identity with no written member (<c>FromQuery(query)</c> alone
    /// installs none and is not reported). A method group (no argument list to read) is reported.
    /// </summary>
    public static readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> IdentityParameters =
        new Dictionary<string, ImmutableDictionary<string, string>>
        {
            ["MeshWeaver.Mesh.Services.MeshQueryRequest"] = new Dictionary<string, string>
            {
                ["FromQuery"] = "userId",
                ["FromQueries"] = "userId",
            }.ToImmutableDictionary(StringComparer.Ordinal),
        }.ToImmutableDictionary(StringComparer.Ordinal);

    // Identifier texts worth binding — a cheap prefilter so the semantic model is asked only about
    // names that could be one of the members above.
    private static readonly ImmutableHashSet<string> CandidateNames =
        Members.Values.Concat(WrittenMembers.Values).SelectMany(s => s)
            .Concat(IdentityParameters.Values.SelectMany(m => m.Keys))
            .ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every reference in <paramref name="compilation"/>'s syntax trees. Pass the compilation of the
    /// AUTHORED source (before platform source generators run) so platform-generated code is not
    /// attributed to the author.
    /// </summary>
    public static ImmutableList<InMeshImpersonationReference> Find(Compilation compilation, CancellationToken ct = default) =>
        Find(compilation, compilation.SyntaxTrees, ct);

    /// <summary>
    /// Every reference in <paramref name="trees"/>, each a syntax tree of <paramref name="compilation"/>.
    /// The compile path passes the AUTHORED trees plus the trees a NODE-SUPPLIED source generator added
    /// (<c>GeneratorPipeline.RunSourceGeneratorsWithNodeOutput</c>'s untrusted output): code a node's own
    /// <c>#r</c>'d generator emits is the author's code, however it was produced. Only the platform's
    /// built-in generators' output is left out.
    /// </summary>
    public static ImmutableList<InMeshImpersonationReference> Find(
        Compilation compilation, IEnumerable<SyntaxTree> trees, CancellationToken ct = default)
    {
        var found = ImmutableList.CreateBuilder<InMeshImpersonationReference>();
        // 🚨 A using ALIAS renames a type (`using D = MeshWeaver.Messaging.MessageDelivery<string>;
        // new D(forged, …)`), so the constructor prefilter also accepts every alias name declared in
        // ANY tree of the compilation (a `global using` alias reaches every file). An alias can only
        // be introduced by a using directive, so this set is complete and the binding below decides.
        var constructorNames = ConstructorTypeNames.Union(compilation.SyntaxTrees
            .SelectMany(t => t.GetRoot(ct).DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .OfType<UsingDirectiveSyntax>())
            .Where(u => u.Alias is not null)
            .Select(u => u.Alias!.Name.Identifier.ValueText));
        foreach (var tree in trees)
        {
            ct.ThrowIfCancellationRequested();
            SemanticModel? model = null;
            foreach (var node in tree.GetRoot(ct).DescendantNodes())
            {
                switch (node)
                {
                    // A constructor call: an explicit `new MessageDelivery<T>(…)` (prefiltered by its
                    // type name), a target-typed `new(…)` with enough arguments to carry a sender, or
                    // a derived type's `: base(…)`. The last two are rare and are bound.
                    case ObjectCreationExpressionSyntax creation
                        when constructorNames.Contains(RightmostName(creation.Type)):
                    case ImplicitObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: >= 3 }:
                    case ConstructorInitializerSyntax:
                    case PrimaryConstructorBaseTypeSyntax:
                        model ??= compilation.GetSemanticModel(tree);
                        if (model.GetSymbolInfo(node, ct).Symbol is IMethodSymbol
                            {
                                MethodKind: MethodKind.Constructor, Parameters.Length: > 0,
                            } constructor
                            && Constructors.Contains(TypeName(constructor.ContainingType.OriginalDefinition)))
                            found.Add(new($"{TypeName(constructor.ContainingType.OriginalDefinition)}..ctor", Where(node)));
                        break;

                    case LiteralExpressionSyntax literal
                        when literal.IsKind(SyntaxKind.StringLiteralExpression)
                             && string.Equals(literal.Token.ValueText, SystemLiteral, StringComparison.OrdinalIgnoreCase):
                        found.Add(new($"\"{SystemLiteral}\"", Where(literal)));
                        break;

                    // A name inside a QualifiedNameSyntax stands in a namespace-or-type position
                    // (`using System.Linq;`, `System.Collections.Generic.List<T>`): it can never be a
                    // field, property or method, so it is not bound at all. That is what keeps the
                    // candidate `System` (WellKnownUsers.System) from costing a semantic query per
                    // using directive.
                    case SimpleNameSyntax name when CandidateNames.Contains(name.Identifier.ValueText)
                                                    && name.Parent is not QualifiedNameSyntax:
                        model ??= compilation.GetSemanticModel(tree);
                        var info = model.GetSymbolInfo(name, ct);
                        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                        if (symbol is null || symbol.ContainingType is not { } type)
                            break;
                        var typeName = TypeName(type.OriginalDefinition);
                        var member = symbol.Name;
                        if (Members.TryGetValue(typeName, out var always) && always.Contains(member))
                            found.Add(new($"{typeName}.{member}", Where(name)));
                        else if (WrittenMembers.TryGetValue(typeName, out var written) && written.Contains(member)
                                 && IsWritten(name))
                            found.Add(new($"{typeName}.{member} (written)", Where(name)));
                        else if (IdentityParameters.TryGetValue(typeName, out var factories)
                                 && factories.TryGetValue(member, out var parameter)
                                 && Supplies(name, symbol, parameter))
                            found.Add(new($"{typeName}.{member} ({parameter} supplied)", Where(name)));
                        break;
                }
            }
        }
        return found.ToImmutable();
    }

    private static string RightmostName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string TypeName(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{type.Name}" : type.Name;

    // The name is the TARGET of an assignment — `x.UserId = …`, `new … { UserId = … }`, `r with { UserId = … }`.
    private static bool IsWritten(SimpleNameSyntax name)
    {
        SyntaxNode target = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
        return target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target;
    }

    // The reference passes an argument for `parameter`: by name, or by position. A reference that is
    // not an invocation (a method group handed on) has no argument list to read and counts as
    // supplying it — whoever invokes the delegate decides, and that is out of the author's source.
    private static bool Supplies(SimpleNameSyntax name, ISymbol symbol, string parameter)
    {
        if (symbol is not IMethodSymbol method)
            return false;
        var index = method.Parameters.Select((p, i) => (p, i))
            .Where(x => x.p.Name == parameter).Select(x => (int?)x.i).FirstOrDefault();
        if (index is not { } position)
            return false;
        SyntaxNode callee = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
        if (callee.Parent is not InvocationExpressionSyntax invocation || invocation.Expression != callee)
            return true;
        var arguments = invocation.ArgumentList.Arguments;
        return arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == parameter)
               || arguments.Where(a => a.NameColon is null).Skip(position).Any();
    }

    private static string Where(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{span.Path}({span.StartLinePosition.Line + 1})";
    }
}
