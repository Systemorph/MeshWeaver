using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Fail-closed content-integrity guard for Create/Update: rejects a write whose
/// <see cref="MeshNode.Content"/> is a raw JSON object carrying a polymorphic
/// <c>$type</c> discriminator that no registry will EVER resolve.
///
/// <para><b>The defect this kills (atioz 2026-06-12, image catalog2-20260612):</b>
/// an agent created Markdown nodes under a Space with
/// <c>content: {"$type": "MarkdownConfiguration", "markdown": "…"}</c> — a type
/// name that exists nowhere (the registered Markdown content type is
/// <c>MarkdownContent</c>). The write was accepted verbatim; from then on every
/// load correctly degraded the content to an untyped <see cref="JsonElement"/>
/// (bad-data tolerance), so the node rendered EMPTY, <c>edit_content</c> refused
/// ("content is JsonElement, not editable text"), recycling didn't heal, and
/// JSON-merge patches kept the broken discriminator alive. The data was
/// un-typeable from the moment it was written — the only correct place to stop
/// it is the write boundary, with a speaking error the writing agent can act on.</para>
///
/// <para><b>Scope — framework built-in NodeTypes only.</b> The guard rejects ONLY
/// when the node's <see cref="MeshNode.NodeType"/> resolves to a STATIC node
/// (<see cref="StaticNodeProviderExtensions.FindStaticNode"/>) whose definition
/// does NOT carry a runtime-compile source: every sanctioned content type of a
/// framework built-in (MarkdownContent, CodeConfiguration, Comment,
/// AgentConfiguration, …) is registered on the mesh root's
/// <see cref="MeshWeaver.Domain.ITypeRegistry"/> chain, so an unresolvable
/// discriminator there is definitively garbage. Exempt are (a) DYNAMIC NodeTypes
/// (DB-defined, compiled from a Space's Source — e.g.
/// <c>AgenticPension/Glossarbegriff</c>, no static node) and (b) statically
/// SEEDED but runtime-COMPILED NodeTypes (filesystem/sample definitions whose
/// <c>NodeTypeDefinition.Configuration</c>/<c>HubConfiguration</c> is a compile
/// source string — e.g. <c>Cornerstone/Insured</c> with instance content
/// <c>$type: "Insured"</c>): both register their compiled content types only on
/// their per-node hubs, so this hub legitimately cannot resolve them and the
/// content passes through as a JsonElement to be typed on the owner.</para>
///
/// <para>Bad-data TOLERANCE is unchanged: this guard never throws on reads, and
/// content WITHOUT a <c>$type</c> (free-form JSON blobs) stays legal. Existing
/// broken rows still load (degraded, now loudly logged by
/// <c>MeshNodeTypeSource.ResolveJsonElementContent</c>); only NEW writes of
/// definitively-untypeable content are refused.</para>
/// </summary>
public sealed class ContentDiscriminatorValidator : INodeValidator
{
    private readonly IMessageHub _hub;
    private readonly ILogger<ContentDiscriminatorValidator> _logger;

    public ContentDiscriminatorValidator(
        IMessageHub hub,
        ILogger<ContentDiscriminatorValidator> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Create + Update — the two surfaces that persist content.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Update];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;

        // Typed (in-process) content, null content, or non-object JSON — nothing to judge.
        if (node.Content is not JsonElement je || je.ValueKind != JsonValueKind.Object)
            return Observable.Return(NodeValidationResult.Valid());

        // No $type → free-form JSON blob. Legal: untyped NodeTypes store arbitrary
        // JSON by design; the typed-content pipeline only engages on a discriminator.
        if (!je.TryGetProperty("$type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return Observable.Return(NodeValidationResult.Valid());

        var discriminator = typeProp.GetString();
        if (string.IsNullOrEmpty(discriminator))
            return Observable.Return(NodeValidationResult.Valid());

        if (Resolves(discriminator))
            return Observable.Return(NodeValidationResult.Valid());

        // Unresolvable here — only definitive for BUILT-IN NodeTypes (see class doc).
        // A dynamic NodeType's compiled content types live on its per-node hub only.
        if (string.IsNullOrEmpty(node.NodeType))
            return Observable.Return(NodeValidationResult.Valid());
        var typeNode = _hub.ServiceProvider.FindStaticNode(node.NodeType);
        if (typeNode is null)
            return Observable.Return(NodeValidationResult.Valid());

        // Statically SEEDED but runtime-COMPILED NodeTypes (filesystem/sample
        // definitions like Cornerstone/Insured whose NodeTypeDefinition carries a
        // Configuration / HubConfiguration SOURCE STRING) register their content
        // types (e.g. `$type: "Insured"`) only on the per-node hubs that load the
        // compiled assembly — this hub's registry legitimately cannot resolve
        // them, so they are exempt like DB-defined dynamic NodeTypes. Only
        // framework built-ins (in-process HubConfiguration delegate, no compile
        // source) have ALL their content types on this registry chain.
        if (HasRuntimeCompiledConfiguration(typeNode))
            return Observable.Return(NodeValidationResult.Valid());

        _logger.LogWarning(
            "ContentDiscriminatorGuard: blocked {Operation} of '{Path}' (NodeType '{NodeType}') — " +
            "content discriminator '$type': '{Discriminator}' is not a registered type; the content " +
            "would persist as an untyped blob that renders empty and cannot be edited.",
            context.Operation, node.Path, node.NodeType, discriminator);

        return Observable.Return(NodeValidationResult.Invalid(
            $"Content of '{node.Path}' carries the polymorphic discriminator '$type': '{discriminator}', " +
            $"which is not a registered content type for the built-in NodeType '{node.NodeType}' — it would " +
            $"persist as an untyped blob that renders empty and cannot be edited. Use the NodeType's declared " +
            $"content shape (check '@{node.NodeType}/schema/'); for Markdown nodes the content is " +
            "{\"$type\": \"MarkdownContent\", \"content\": \"<markdown text>\"}."));
    }

    /// <summary>
    /// True when the NodeType definition carries a runtime-compile source string
    /// (<see cref="Configuration.NodeTypeDefinition.Configuration"/> /
    /// <see cref="Configuration.NodeTypeDefinition.HubConfiguration"/>) — its
    /// content types come from a compiled assembly this hub has not loaded.
    /// Handles both the typed shape and the raw-JsonElement shape (a definition
    /// freshly parsed from a filesystem seed can arrive either way).
    /// </summary>
    private static bool HasRuntimeCompiledConfiguration(MeshNode typeNode)
    {
        switch (typeNode.Content)
        {
            case Configuration.NodeTypeDefinition def:
                return !string.IsNullOrWhiteSpace(def.Configuration)
                       || !string.IsNullOrWhiteSpace(def.HubConfiguration);
            case JsonElement je when je.ValueKind == JsonValueKind.Object:
                return (je.TryGetProperty("configuration", out var c)
                            && c.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(c.GetString()))
                       || (je.TryGetProperty("hubConfiguration", out var h)
                            && h.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(h.GetString()));
            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves <paramref name="discriminator"/> against this hub's registry chain,
    /// with the same full-name → short-name fallback the load path applies
    /// (<c>MeshNodeTypeSource.ResolveJsonElementContent</c>): a writer that wasn't
    /// registered serialises under <c>Type.FullName</c>, while readers register the
    /// short name — both spellings must count as resolvable.
    /// </summary>
    private bool Resolves(string discriminator)
    {
        var registry = _hub.TypeRegistry;
        if (registry.TryGetType(discriminator, out var def) && def?.Type is not null)
            return true;

        if (discriminator.Contains('.'))
        {
            var shortName = discriminator[(discriminator.LastIndexOf('.') + 1)..];
            if (registry.TryGetType(shortName, out def) && def?.Type is not null)
                return true;
        }

        return false;
    }
}
