#pragma warning disable CS1591

using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using MeshWeaver.PluginCatalog;   // moved out of MeshWeaver.PluginCatalog.Test (#2276):
                                  // the parent namespace no longer resolves implicitly.

namespace MeshWeaver.AI.Test;

/// <summary>
/// #1984's BACKFILL, answered by measurement rather than by a migration. Eleven skills are stored
/// today as the broken shape the old parser produced — <c>nodeType: Markdown</c> carrying
/// <see cref="MarkdownContent"/> — and the obvious reading of "existing nodes need a retype" is that
/// something has to go and rewrite them: a <c>LogonAction</c>, or (worse) a SQL migration looping
/// partition schemas.
///
/// <para><b>Neither is needed, and this test is why.</b> A package re-install compares the incoming
/// node against the stored one with <see cref="PackageInstaller.IsUnchanged"/> and, when they
/// differ, upserts — and <c>UpdateAccordingToSourceNode</c> applies BOTH
/// <c>NodeType = sourceNode.NodeType ?? state.NodeType</c> and
/// <c>Content = sourceNode.Content ?? state.Content</c>. So the retype and the content replacement
/// are already the upsert's normal behaviour; the only question was whether the unchanged-check
/// would SKIP the write and leave the node broken forever. It does not — for either of the two
/// stored shapes — and that is the whole backfill.</para>
///
/// <para>🚨 The one node this deliberately does NOT reach is a CLAIMED one
/// (<c>SyncBehavior != Include</c>), which <c>DecideAndWrite</c> skips before it ever asks whether
/// anything changed. That is correct and not a gap: claiming is the deliberate act that decouples a
/// node from its package, and a repair that overrode it would clobber a user's edit.</para>
/// </summary>
public class SkillRetypeOnReinstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            // The content compare serializes both sides with the mesh hub's options, so the target
            // type has to be resolvable there — this is what AddSkillType does in a real portal.
            .ConfigureHub(config => config.WithType<SkillDefinition>(nameof(SkillDefinition)));

    private const string Body = "Read the file. Assert the body survived.";

    private static MeshNode Stored(string nodeType, object content) => new("deployment", "Hosting/Skill")
    {
        NodeType = nodeType,
        Name = "/deployment",
        State = MeshNodeState.Active,
        Content = content,
    };

    private static MeshNode Incoming() => Stored(
        SkillNodeType.NodeType, new SkillDefinition { Instructions = Body });

    /// <summary>
    /// The shape stored BEFORE the front-matter casing fix: the node type itself degraded to
    /// Markdown. Caught on the scalar compare, so it never even reaches the content signature.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void AMarkdownTypedSkill_ReadsAsChanged_SoTheReinstallRetypesIt()
    {
        var current = Stored("Markdown", new MarkdownContent { Content = Body });

        PackageInstaller.IsUnchanged(current, Incoming(), Mesh.JsonSerializerOptions)
            .Should().BeFalse(
                "NodeType is part of the scalar compare, so the re-install rewrites the node — "
                + "the retype needs no migration, only a re-import through the fixed parser");
    }

    /// <summary>
    /// The shape stored BETWEEN the two halves of the fix: correctly typed <c>Skill</c>, but still
    /// carrying MarkdownContent — the silently EMPTY skill. The scalars now MATCH, so this one rides
    /// entirely on the content signature; if that compared equal, the node would be skipped on every
    /// re-install and stay empty forever.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ASkillTypedNodeStillHoldingMarkdownContent_ReadsAsChanged()
    {
        var current = Stored(SkillNodeType.NodeType, new MarkdownContent { Content = Body });

        PackageInstaller.IsUnchanged(current, Incoming(), Mesh.JsonSerializerOptions)
            .Should().BeFalse(
                "same node type, different content type — the signature compare is the only thing "
                + "standing between an empty skill and its instructions");
    }

    /// <summary>The other half of the claim: a skill that is ALREADY right is not rewritten. Without
    /// this the two assertions above would also pass for an installer that rewrote everything, every
    /// time — which is the idempotence regression InstallSignatureAlignmentTest exists to prevent.</summary>
    [Fact(Timeout = 30000)]
    public void AnAlreadyCorrectSkill_IsNotRewritten()
    {
        PackageInstaller.IsUnchanged(Incoming(), Incoming(), Mesh.JsonSerializerOptions)
            .Should().BeTrue("a re-install of an unchanged skill must write nothing");
    }
}
