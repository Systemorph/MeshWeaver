#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Content of a keying-probe node. Shaped after a real <c>LanguageModel</c> node (the family whose
/// ids carry the provider's slash) so the write verbs exercise the same typed-content path they do
/// in production — <c>Update</c> REFUSES a node with null content, so a content-less probe would
/// never reach the keying logic this test is about.
/// </summary>
public record LanguageModelProbeContent
{
    /// <summary>The provider endpoint the model is served from.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Display ordering among sibling models.</summary>
    public int Order { get; init; }
}

/// <summary>
/// #3894: an MCP write to a node whose <see cref="MeshNode.Id"/> CONTAINS A SLASH re-keyed it.
///
/// <para><b>The defect.</b> <c>MeshOperations.SanitizeNodeId</c> split a slash-bearing id at its
/// LAST slash and moved the prefix into the namespace, on the stated premise that "the DB has a
/// CHECK constraint blocking slashes in id". There is no such constraint — every <c>mesh_nodes</c>
/// DDL declares plain <c>id TEXT NOT NULL</c>. What the split actually did was change the node's
/// IDENTITY while leaving its PATH untouched, because <c>path</c> is a GENERATED column
/// (<c>namespace || '/' || id</c>) and moving a slash across the two is path-invariant.</para>
///
/// <para><b>Why that is data loss.</b> The primary key is <c>(namespace, id)</c>, and writes upsert
/// <c>ON CONFLICT (namespace, id)</c> — so a re-keyed write found NO conflict and INSERTED a SECOND
/// row carrying the same generated path. Reads and deletes address <c>WHERE path = $1</c>, so the
/// read then resolved an arbitrary one of the two rows and a delete removed BOTH. On production
/// this split <c>Provider/OpenRouter</c> + <c>z-ai/glm-5.3</c> — a LanguageModel node whose id is
/// the provider's wire id, exactly like its eleven siblings — across two rows, after which it
/// vanished. Slash-bearing ids are not an accident; the Postgres adapter says so in its own words
/// ("THERE IS NO POSITIONAL (namespace, id) SPLIT OF A PATH — an id may contain '/'", #2212).</para>
///
/// <para>🚨 <b>What this test can and cannot observe, stated honestly.</b> The monolith's
/// <c>InMemoryStorageAdapter</c> holds nodes in a PATH-KEYED dictionary, so the duplicate ROW
/// cannot manifest here — the second write simply overwrites the first entry. What IS observable,
/// and what this test asserts, is the node's <c>Namespace</c>/<c>Id</c> pair, which IS the Postgres
/// primary key: if identity survives a write, the upsert targets the same PK and there is one row;
/// if it is re-keyed, Postgres inserts a second. Asserting the keying is therefore the faithful
/// in-memory expression of "ONE row with the ORIGINAL keying", not a weaker substitute.</para>
///
/// <para>Every case drives the REAL MCP verb (<c>MeshOperations.Create</c> / <c>Update</c> /
/// <c>Patch</c>) against a REAL monolith mesh, and the update case reproduces the operator sequence
/// from the incident exactly: read the node back, change one field, write the whole node again.</para>
/// </summary>
public class SlashBearingNodeIdKeyingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The node type the probe nodes carry — registered below so the create path
    /// (which rejects an unregistered NodeType outright) accepts them.</summary>
    private const string ProbeNodeType = "LanguageModelKeyingProbe";

    /// <summary>The partition root both provider namespaces live under.</summary>
    private const string PartitionRoot = "Provider";

    // ── the incident's own shape: an id that legitimately contains a slash ──
    private const string SlashNamespace = "Provider/OpenRouter";
    private const string SlashId = "z-ai/glm-5.3";
    private const string SlashPath = $"{SlashNamespace}/{SlashId}";

    // ── the positive control: an ordinary, slash-free id ──
    private const string PlainNamespace = "Provider/OpenAI";
    private const string PlainId = "gpt-5-2";
    private const string PlainPath = $"{PlainNamespace}/{PlainId}";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(ProbeNodeType)
            {
                Name = "Language Model Keying Probe",
                IsSatelliteType = false,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source
                        .WithContentType<LanguageModelProbeContent>()),
            })
            .ConfigureHub(config => config
                .WithType<LanguageModelProbeContent>(nameof(LanguageModelProbeContent)));

    /// <summary>
    /// 🚨 THE TWIN THE ISSUE ASKS FOR. Patch a slash-id node through
    /// <c>MeshOperations.Patch</c> and assert it is still ONE node under its ORIGINAL
    /// <c>(namespace, id)</c>, on a single version chain.
    ///
    /// <para>This case is a REGRESSION GUARD rather than a discriminating repro, and saying so is
    /// part of the test: <c>Patch</c> never called the sanitiser. It reads the existing node and
    /// writes <c>existing with { … }</c>, so it inherits that node's keying by construction — which
    /// is exactly why the issue's own follow-up comment could not reproduce the split through
    /// <c>patch</c> alone. The invariant is pinned here so a future refactor that routes <c>Patch</c>
    /// through a shared "normalise the node" step cannot silently acquire the defect.</para>
    /// </summary>
    [Fact(Timeout = 120_000)] // literal: an attribute argument must be a constant. Dominates TestTimeouts.Convergence.
    public async Task Patch_OnASlashBearingId_KeepsTheOriginalKeying_AndOneVersionChain()
    {
        var seeded = await SeedProbe(SlashId, SlashNamespace, "GLM 5.3");

        var result = await Operations()
            .Patch(SlashPath, """{"name":"GLM 5.3 (patched)"}""")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"Patch tool returned: {result}");
        result.Should().NotContain("Error:",
            "patching the display name of an existing node is an applicable change and must not be refused");

        var after = await ReadNodeOnce(SlashPath);

        after.Name.Should().Be("GLM 5.3 (patched)",
            "the tool reported success, so the caller's own field must actually have landed");
        after.Namespace.Should().Be(SlashNamespace,
            "the namespace is half the (namespace, id) PRIMARY KEY — a patch must address the existing "
            + "row, never mint a new identity at the same path");
        after.Id.Should().Be(SlashId,
            "the id is the other half of the PRIMARY KEY and it legitimately contains a slash "
            + "(a LanguageModel id is the provider's wire id); re-keying it to 'glm-5.3' under "
            + "namespace 'Provider/OpenRouter/z-ai' leaves the generated path identical while "
            + "INSERTING a second row — the #3894 data loss");
        after.Path.Should().Be(SlashPath,
            "the path is generated from the keying and must be unchanged either way");
        after.Version.Should().BeGreaterThan(seeded.Version,
            "the write must ADVANCE the existing node's version chain — a re-keyed write would start "
            + "a second chain at version 1 instead, which is how one path came to hold two 'version 1' rows");
    }

    /// <summary>
    /// The DISCRIMINATING repro for <c>update</c> — this is the verb the sanitiser ran on, and this
    /// case fails on the pre-#3894 code. It reproduces the operator sequence from the incident
    /// literally: read the node back, change one field, write the WHOLE node again (which is what an
    /// MCP client does, and which carries the node's own slash-bearing id straight back in).
    /// </summary>
    [Fact(Timeout = 120_000)] // literal: an attribute argument must be a constant. Dominates TestTimeouts.Convergence.
    public async Task Update_OnASlashBearingId_KeepsTheOriginalKeying_AndOneVersionChain()
    {
        var seeded = await SeedProbe(SlashId, SlashNamespace, "GLM 5.3");

        var result = await Operations()
            .Update(SerializeAsUpdateBatch(seeded with { Name = "GLM 5.3 (updated)" }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"Update tool returned: {result}");
        result.Should().NotContain("Error:",
            "writing back a node that was just read, with one field changed, must not be refused");

        var after = await ReadNodeOnce(SlashPath);

        after.Name.Should().Be("GLM 5.3 (updated)",
            "the tool reported success, so the caller's own field must actually have landed");
        after.Namespace.Should().Be(SlashNamespace,
            "update must write back the SAME (namespace, id) the caller read — SanitizeNodeId used to "
            + "move 'z-ai' out of the id and into the namespace here, producing "
            + "'Provider/OpenRouter/z-ai' and a second PRIMARY KEY at an unchanged path");
        after.Id.Should().Be(SlashId,
            "the id must survive the round trip verbatim, slash included — this is the exact "
            + "re-keying that split the production node across two rows (#3894)");
        after.Path.Should().Be(SlashPath,
            "the path is generated from the keying and is invariant under the split, which is "
            + "precisely why the corruption was invisible to every path-addressed read");
        after.Version.Should().BeGreaterThan(seeded.Version,
            "the write must ADVANCE the existing node's version chain rather than start a second one");
    }

    /// <summary>
    /// The DISCRIMINATING repro for <c>create</c> — the other verb the sanitiser ran on. A caller
    /// that creates a node at a slash-bearing id must get a node keyed the way it asked, not one
    /// silently re-keyed to a namespace that does not match its siblings.
    /// </summary>
    [Fact(Timeout = 120_000)] // literal: an attribute argument must be a constant. Dominates TestTimeouts.Convergence.
    public async Task Create_WithASlashBearingId_StoresItUnderTheKeyingItWasGiven()
    {
        await SeedPartitionRoot();

        var result = await Operations()
            .Create(NodeJson(SlashId, SlashNamespace, "GLM 5.3"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"Create tool returned: {result}");
        result.Should().NotContain("Error:",
            "an id containing a slash is legitimate — every LanguageModel id is the provider's wire id");

        var created = await ReadNodeOnce(SlashPath);

        created.Namespace.Should().Be(SlashNamespace,
            "the node must be created under the namespace the caller gave, not one the platform "
            + "invented by splitting the id at its last slash");
        created.Id.Should().Be(SlashId,
            "the id must be stored verbatim; storing 'glm-5.3' instead makes this node key "
            + "differently from its eleven seeded siblings and makes it unaddressable by the "
            + "identity its creator holds (#3894)");
        created.Path.Should().Be(SlashPath,
            "the generated path must match what the caller asked for");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL, in the other direction: an ORDINARY, slash-free id must still be
    /// keyed exactly as before through both write verbs. Without this the keying fix could pass by
    /// disabling identity handling altogether — a test that only checks "nothing was rewritten"
    /// cannot tell a correct writer from one that writes nothing at all.
    /// </summary>
    [Fact(Timeout = 120_000)] // literal: an attribute argument must be a constant. Dominates TestTimeouts.Convergence.
    public async Task Create_ThenUpdate_WithAnOrdinaryId_StillKeysAndLandsExactlyAsBefore()
    {
        await SeedPartitionRoot();

        var createResult = await Operations()
            .Create(NodeJson(PlainId, PlainNamespace, "GPT 5.2"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"Create tool returned: {createResult}");
        createResult.Should().NotContain("Error:",
            "an ordinary slash-free id is the common case and must keep working unchanged");

        var created = await ReadNodeOnce(PlainPath);
        created.Namespace.Should().Be(PlainNamespace,
            "an ordinary id must still be keyed by the namespace it was given — the fix removes a "
            + "wrong rewrite, it does not stop the platform keying nodes at all");
        created.Id.Should().Be(PlainId,
            "an ordinary id must be stored verbatim, exactly as it always was");
        created.Path.Should().Be(PlainPath,
            "the generated path must match what the caller asked for");

        var updateResult = await Operations()
            .Update(SerializeAsUpdateBatch(created with { Name = "GPT 5.2 (updated)" }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"Update tool returned: {updateResult}");
        updateResult.Should().NotContain("Error:",
            "writing back an ordinary node with one field changed must not be refused");

        var updated = await ReadNodeOnce(PlainPath);
        updated.Name.Should().Be("GPT 5.2 (updated)",
            "the tool reported success, so the caller's own field must actually have landed");
        updated.Namespace.Should().Be(PlainNamespace,
            "an ordinary id must round-trip through update with its keying intact");
        updated.Id.Should().Be(PlainId,
            "an ordinary id must round-trip through update verbatim");
        updated.Version.Should().BeGreaterThan(created.Version,
            "the update must advance the SAME version chain, proving the write addressed the "
            + "existing row rather than creating a parallel one");
    }

    // ── helpers ──

    private MeshOperations Operations() => new(Mesh);

    /// <summary>The MCP <c>create</c> payload for one probe node — the JSON an agent actually sends.</summary>
    // $$$ / {{{…}}}: the payload itself contains literal `{` and `}}`, so the interpolation
    // delimiters have to be longer than any brace run in the JSON (CS9007 otherwise).
    private static string NodeJson(string id, string ns, string name)
        => $$$"""
            {"id":{{{JsonSerializer.Serialize(id)}}},"namespace":{{{JsonSerializer.Serialize(ns)}}},
             "nodeType":"{{{ProbeNodeType}}}","name":{{{JsonSerializer.Serialize(name)}}},
             "content":{"endpoint":"https://example.invalid/v1","order":1}}
            """;

    /// <summary>
    /// The MCP <c>update</c> payload: the node serialised with the mesh's OWN options and wrapped in
    /// the array the tool takes. This is deliberately a re-serialisation of a node that was READ
    /// back — the operator sequence from the incident — so the slash-bearing id travels into the
    /// write exactly as a real client would carry it.
    /// </summary>
    private string SerializeAsUpdateBatch(MeshNode node)
        => $"[{JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions)}]";

    /// <summary>
    /// Seeds the top-level <c>Provider</c> partition root as System. A top-level node IS a partition
    /// root, so <c>PartitionWriteGuardValidator</c> refuses a non-System caller creating one whose
    /// NodeType does not own a partition — see <see cref="MonolithMeshTestBase.SeedTopLevel"/>.
    /// </summary>
    private Task<MeshNode> SeedPartitionRoot()
        => SeedTopLevel(new MeshNode(PartitionRoot)
        {
            Name = "Providers",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        });

    /// <summary>Seeds one probe node under <see cref="PartitionRoot"/> and returns it as stored.</summary>
    private async Task<MeshNode> SeedProbe(string id, string ns, string name)
    {
        await SeedPartitionRoot();
        await SeedTopLevel(new MeshNode(id, ns)
        {
            NodeType = ProbeNodeType,
            Name = name,
            State = MeshNodeState.Active,
            // Content must be non-null: Update refuses a null-content node outright, so a
            // content-less probe would never reach the keying logic under test.
            Content = new LanguageModelProbeContent { Endpoint = "https://example.invalid/v1", Order = 1 },
        });
        return await ReadNodeOnce($"{ns}/{id}");
    }

    /// <summary>
    /// One authoritative read off the shared per-node stream handle — the same primitive an MCP
    /// <c>get</c> uses, and the only read that is live rather than eventually consistent.
    /// </summary>
    private Task<MeshNode> ReadNodeOnce(string path)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(node => node is not null)
            .Select(node => node!)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
}
