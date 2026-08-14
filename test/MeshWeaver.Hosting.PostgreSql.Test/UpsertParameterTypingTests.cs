using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// The compare-and-set write path must not depend on PostgreSQL INFERRING a parameter's type.
///
/// <para><b>The defect.</b> <c>BuildUpsertAsync</c> lays its parameters out POSITIONALLY for the
/// <c>INSERT … ON CONFLICT</c> form, and the compare-and-set form
/// (<see cref="PostgreSqlStorageAdapter.WriteIfVersion"/> with a non-zero expected version) reuses
/// that identical list against a plain <c>UPDATE</c>. That UPDATE deliberately omits
/// <c>created_by</c> ($17) and <c>created_date</c> ($19) — authorship is immutable, set once at
/// INSERT — so those two parameters are BOUND but never REFERENCED. PostgreSQL infers a
/// parameter's type from where it is used, and a parameter that appears nowhere in the statement
/// has no context to infer from. Npgsql's <c>AddWithValue(DBNull.Value)</c> sends the unspecified
/// OID 0, so the server has nothing at all and fails the whole statement with
/// <c>42P18: could not determine data type of parameter $17</c>.</para>
///
/// <para><b>Why only framework-written nodes.</b> A non-null <c>CreatedBy</c> makes Npgsql send the
/// text OID, which the server accepts even for an unreferenced parameter. Only a node whose
/// authorship is null — one the framework wrote for itself, such as the build-claim lock — reaches
/// the untyped case. That is how this reached production as a portal-readiness wedge: every
/// build-claim arbitration pass past the very first (which takes the insert-only path, where every
/// parameter IS referenced) failed, so no builder was ever elected and the NodeType bake never
/// started.</para>
///
/// <para><b>Fail-before.</b> Against the pre-fix adapter the first two tests throw
/// <c>PostgresException 42P18</c> naming $17 and $19 respectively. The remaining tests pass both
/// before and after: they pin the boundary of the defect — a REFERENCED <c>DBNull</c> infers fine,
/// and the satellite layout references every parameter it binds — so the fix cannot be narrowed by
/// accident later.</para>
/// </summary>
[Collection("PostgreSql")]
public class UpsertParameterTypingTests(PostgreSqlFixture fixture)
{
    private readonly JsonSerializerOptions options = new();

    private const string ClaimNamespace = "Admin/Build/_Claim";
    private const string ClaimPath = "Admin/Build/_Claim/Claim";

    /// <summary>
    /// A node with every optional column populated EXCEPT the authorship pair — the shape the
    /// framework writes for its own coordination nodes (the build claim, the GO marker).
    /// </summary>
    private static MeshNode SystemAuthoredNode(long version, string name) =>
        new("Claim", ClaimNamespace)
        {
            Name = name,
            Description = "every earlier optional column is set, so $17 is the FIRST untyped one",
            NodeType = "Build",
            Category = "coordination",
            Icon = "/static/NodeTypeIcons/task-list.svg",
            Order = 3,
            LastModified = DateTimeOffset.UtcNow,
            Version = version,
            Content = new { claimedBy = "silo-a" },
            DesiredId = "Claim",
            ExcludeFromContext = new HashSet<string> { "create" },
            // CreatedBy / LastModifiedBy / CreatedDate deliberately unset: nothing stamps
            // authorship on a node the framework writes for itself.
        };

    /// <summary>
    /// 🚨 THE PRODUCTION WEDGE. The build-claim arbiter commits through
    /// <see cref="PostgreSqlStorageAdapter.WriteIfVersion"/> against the lock's current version, so
    /// every pass after the lock row exists takes the compare-and-set UPDATE. Pre-fix this threw
    /// <c>42P18 … parameter $17</c> on every pass — no builder elected, no bake, no readiness.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CompareAndSet_AppliesWhenAuthorshipIsNull()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = fixture.StorageAdapter;

        // The insert-only leg — the one that always worked, because the INSERT references every
        // parameter it binds.
        var created = await adapter.WriteIfVersion(SystemAuthoredNode(1, "held by silo-a"), 0, options)
            .Should().Within(30.Seconds()).Emit();
        created.Should().BeTrue();

        // The heartbeat / re-grant leg — compare-and-set against the version just written.
        var applied = await adapter.WriteIfVersion(SystemAuthoredNode(2, "held by silo-b"), 1, options)
            .Should().Within(30.Seconds()).Emit();
        applied.Should().BeTrue();

        var stored = await adapter.Read(ClaimPath, options).Should().Within(30.Seconds()).Emit();
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("held by silo-b");
        stored.Version.Should().Be(2L);
        // Authorship stays immutable — and absent, which is the whole point.
        stored.CreatedBy.Should().BeNull();
    }

    /// <summary>
    /// The discriminator: with <see cref="MeshNode.CreatedBy"/> SET, $17 carries the text OID and
    /// the server accepts it even unreferenced — so the failure moves to $19
    /// (<c>created_date</c>), the other parameter the compare-and-set UPDATE binds without
    /// referencing. This is what proves the defect is "unreferenced parameter, no type" rather
    /// than "CreatedBy specifically": fixing one column would have left the other armed.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CompareAndSet_AppliesWhenOnlyCreatedDateIsMissing()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = fixture.StorageAdapter;

        static MeshNode Authored(long version, string name) =>
            SystemAuthoredNode(version, name) with
            {
                CreatedBy = "system-security",
                LastModifiedBy = "system-security",
                // CreatedDate left at default → DBNull at $19.
            };

        var created = await adapter.WriteIfVersion(Authored(1, "first"), 0, options)
            .Should().Within(30.Seconds()).Emit();
        created.Should().BeTrue();

        var applied = await adapter.WriteIfVersion(Authored(2, "second"), 1, options)
            .Should().Within(30.Seconds()).Emit();
        applied.Should().BeTrue();

        var stored = await adapter.Read(ClaimPath, options).Should().Within(30.Seconds()).Emit();
        stored!.Name.Should().Be("second");
    }

    /// <summary>
    /// The compare-and-set REFUSAL must stay a refusal, not become an exception: a stale expected
    /// version returns false and leaves the durable row alone. Typing the parameters must not
    /// change that verdict — it is the whole cross-cluster exclusivity guarantee.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CompareAndSet_RefusesAStaleExpectedVersion()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = fixture.StorageAdapter;

        await adapter.WriteIfVersion(SystemAuthoredNode(5, "winner"), 0, options)
            .Should().Within(30.Seconds()).Emit();

        var refused = await adapter.WriteIfVersion(SystemAuthoredNode(6, "loser"), 4, options)
            .Should().Within(30.Seconds()).Emit();
        refused.Should().BeFalse();

        var stored = await adapter.Read(ClaimPath, options).Should().Within(30.Seconds()).Emit();
        stored!.Name.Should().Be("winner");
    }

    /// <summary>
    /// The satellite (non-<c>mesh_nodes</c>) layout: 15 parameters plus the expected version, and
    /// the compare-and-set UPDATE references all sixteen — sync_behavior, authorship and
    /// exclude_from_context do not exist on a satellite table, so they are never bound. This path
    /// therefore has NO unreferenced parameter and passed even pre-fix; the test pins that,
    /// because the positional layout differs and the fix must hold for both shapes.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CompareAndSet_AppliesOnASatelliteTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = new PartitionDefinition
        {
            Namespace = "TypingOrg",
            DataSource = "default",
            Schema = "param_typing_test",
            Versioned = true,
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
        };
        var (_, adapter) = await fixture.CreateSchemaAdapterAsync("param_typing_test", partition, ct);

        static MeshNode Satellite(long version, string name) =>
            new("alice_Access", "TypingOrg/_Access")
            {
                Name = name,
                NodeType = "AccessAssignment",
                MainNode = "TypingOrg",
                Version = version,
                Content = new { accessObject = "alice" },
            };

        var created = await adapter.WriteIfVersion(Satellite(1, "first"), 0, options)
            .Should().Within(30.Seconds()).Emit();
        created.Should().BeTrue();

        var applied = await adapter.WriteIfVersion(Satellite(2, "second"), 1, options)
            .Should().Within(30.Seconds()).Emit();
        applied.Should().BeTrue();

        var stored = await adapter.Read("TypingOrg/_Access/alice_Access", options)
            .Should().Within(30.Seconds()).Emit();
        stored!.Name.Should().Be("second");
    }

    /// <summary>
    /// The falsifier for the obvious-but-wrong reading of the incident ("<c>DBNull</c> yields an
    /// untyped parameter, so every optional column is armed"). Every optional column null, through
    /// the ordinary upsert — and it applies, because the INSERT references each parameter and the
    /// server infers each type from its target column. <c>DBNull</c> is not the defect; binding a
    /// parameter the statement never mentions is.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task OrdinaryUpsert_AppliesWithEveryOptionalColumnNull()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = fixture.StorageAdapter;

        var bare = new MeshNode("Bare", "Admin");   // name, description, content, authorship … all null
        await adapter.Write(bare, options).Should().Within(30.Seconds()).Emit();

        var stored = await adapter.Read("Admin/Bare", options).Should().Within(30.Seconds()).Emit();
        stored.Should().NotBeNull();
        stored!.Name.Should().BeNull();
        stored.CreatedBy.Should().BeNull();
    }
}
