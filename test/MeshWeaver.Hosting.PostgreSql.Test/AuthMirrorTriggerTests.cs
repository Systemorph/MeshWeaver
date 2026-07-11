using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Npgsql;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Integration test for the auth-mirror trigger installed by
/// <c>V27_RenameUserSchemaToAuthAndMirrorApiTokens</c>. The trigger fires
/// on every partition's <c>mesh_nodes</c> table and mirrors INSERT / UPDATE
/// / DELETE on rows of type <c>User / Group / Role / VUser / ApiToken</c>
/// into <c>auth.mesh_nodes</c>, preserving the source <c>(namespace, id)</c>.
/// This is the "pure lookup table" the auth queries
/// (<c>GetTokensForUser</c>, <c>UserIdentityCache</c>) read from to avoid
/// fan-out across per-user partitions.
///
/// <para>Tests cover the three mirrored events end-to-end on a real PG
/// instance:</para>
/// <list type="bullet">
///   <item>INSERT into source partition → row appears in <c>auth.mesh_nodes</c></item>
///   <item>UPDATE source row (e.g. <c>RevokeToken</c> flips <c>IsRevoked</c>) →
///         <c>auth.mesh_nodes</c> reflects the new state</item>
///   <item>DELETE source row → <c>auth.mesh_nodes</c> entry removed</item>
/// </list>
///
/// <para>Also verifies the trigger ignores non-auth nodeTypes
/// (e.g. <c>Story</c>) so the mirror stays a focused index, and that
/// inserts into the <c>auth</c> schema itself are not re-mirrored
/// (would loop).</para>
/// </summary>
[Collection("PostgreSql")]
public class AuthMirrorTriggerTests
{
    private readonly PostgreSqlFixture _fixture;

    public AuthMirrorTriggerTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Installs the V27 trigger function + creates the auth schema + wires
    /// the mirror trigger on the public schema's mesh_nodes table. The
    /// fixture does not run migrations directly; this is the minimal
    /// SQL the test needs to exercise the trigger end-to-end.
    /// </summary>
    private async Task EnsureMirrorInstalledAsync()
    {
        await using (var cmd = _fixture.DataSource.CreateCommand("""CREATE SCHEMA IF NOT EXISTS "auth";"""))
            await cmd.ExecuteNonQueryAsync();

        // mesh_nodes in auth needs the same shape as the source partition
        // tables. Reuse the standard initialiser so we don't drift.
        var opts = new PostgreSqlStorageOptions { Schema = "auth" };
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = "auth,public",
            MaxPoolSize = 1
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dsBuilder.UseVector();
        await using var authDs = dsBuilder.Build();
        await PostgreSqlSchemaInitializer.InitializeAsync(authDs, opts);

        // Single-source the trigger function from the initializer's constant — the same script
        // every startup runs. An inline copy here is exactly how the V28 'Space' extension was
        // silently lost in production (three hand-maintained copies drifting apart); the test
        // must exercise THE deployed function body, not its own fork.
        await using (var cmd = _fixture.DataSource.CreateCommand(
            PostgreSqlSchemaInitializer.GetAuthMirrorFunctionScript()))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = _fixture.DataSource.CreateCommand("""
            DROP TRIGGER IF EXISTS mesh_node_mirror_access_objects ON public.mesh_nodes;
            CREATE TRIGGER mesh_node_mirror_access_objects
                AFTER INSERT OR UPDATE OR DELETE ON public.mesh_nodes
                FOR EACH ROW EXECUTE FUNCTION public.mirror_access_object_to_auth_schema();
            """))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<bool> ExistsInAuthAsync(string ns, string id)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            """SELECT EXISTS (SELECT 1 FROM "auth".mesh_nodes WHERE namespace = $1 AND id = $2)""");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string?> ReadNameFromAuthAsync(string ns, string id)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            """SELECT name FROM "auth".mesh_nodes WHERE namespace = $1 AND id = $2""");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task ApiTokenInsert_MirrorsIntoAuthSchema()
    {
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        await EnsureMirrorInstalledAsync().Run().Should().Within(60.Seconds()).Emit();

        var node = new MeshNode("tokenhash123", "rbuergi/ApiToken")
        {
            Name = "Test Token",
            NodeType = "ApiToken"
        };
        await _fixture.StorageAdapter.Write(node, new()).Should().Within(30.Seconds()).Emit();

        await ExistsInAuthAsync("rbuergi/ApiToken", "tokenhash123").Run()
            .Should().Within(30.Seconds()).Be(true);
        await ReadNameFromAuthAsync("rbuergi/ApiToken", "tokenhash123").Run()
            .Should().Within(30.Seconds()).Be("Test Token");
    }

    [Fact]
    public async Task ApiTokenUpdate_PropagatesToMirror()
    {
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        await EnsureMirrorInstalledAsync().Run().Should().Within(60.Seconds()).Emit();

        var node = new MeshNode("tokenhash456", "rbuergi/ApiToken")
        {
            Name = "Active Token",
            NodeType = "ApiToken"
        };
        await _fixture.StorageAdapter.Write(node, new()).Should().Within(30.Seconds()).Emit();

        // Same (namespace, id), different name — simulates the RevokeToken
        // flow that updates Content.IsRevoked while leaving the path fixed.
        var revoked = new MeshNode("tokenhash456", "rbuergi/ApiToken")
        {
            Name = "Revoked Token",
            NodeType = "ApiToken"
        };
        await _fixture.StorageAdapter.Write(revoked, new()).Should().Within(30.Seconds()).Emit();

        await ReadNameFromAuthAsync("rbuergi/ApiToken", "tokenhash456").Run()
            .Should().Within(30.Seconds()).Be("Revoked Token");
    }

    [Fact]
    public async Task ApiTokenDelete_RemovesFromMirror()
    {
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        await EnsureMirrorInstalledAsync().Run().Should().Within(60.Seconds()).Emit();

        var node = new MeshNode("tokenhash789", "rbuergi/ApiToken")
        {
            Name = "To Delete",
            NodeType = "ApiToken"
        };
        await _fixture.StorageAdapter.Write(node, new()).Should().Within(30.Seconds()).Emit();
        await ExistsInAuthAsync("rbuergi/ApiToken", "tokenhash789").Run().Should().Within(30.Seconds()).Be(true);

        await _fixture.StorageAdapter.Delete("rbuergi/ApiToken/tokenhash789").Should().Within(30.Seconds()).Emit();

        await ExistsInAuthAsync("rbuergi/ApiToken", "tokenhash789").Run()
            .Should().Within(30.Seconds()).Be(false);
    }

    [Fact]
    public async Task UserInsert_MirrorsIntoAuthSchema()
    {
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        await EnsureMirrorInstalledAsync().Run().Should().Within(60.Seconds()).Emit();

        // Per the partition convention: a user node sits at the root of its
        // partition with namespace="" and id=userId.
        var user = new MeshNode("rbuergi", "")
        {
            Name = "Roland Bürgi",
            NodeType = "User"
        };
        await _fixture.StorageAdapter.Write(user, new()).Should().Within(30.Seconds()).Emit();

        await ExistsInAuthAsync("", "rbuergi").Run()
            .Should().Within(30.Seconds()).Be(true);
    }

    /// <summary>
    /// A Space root (partition root: namespace = "", id = space id) must mirror into
    /// <c>auth.mesh_nodes</c> — that mirror IS the Spaces catalog. Pins the V28 'Space'
    /// extension against the drift that dropped it in production (spaces created after a
    /// restart never appeared in the catalog): the initializer's script, which this test
    /// installs verbatim, must mirror Space on insert AND remove it on delete.
    /// </summary>
    [Fact]
    public async Task SpaceInsert_MirrorsIntoAuthSchema_AndDeleteRemoves()
    {
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        await EnsureMirrorInstalledAsync().Run().Should().Within(60.Seconds()).Emit();

        var space = new MeshNode("Chess", "")
        {
            Name = "Chess",
            NodeType = "Space"
        };
        await _fixture.StorageAdapter.Write(space, new()).Should().Within(30.Seconds()).Emit();

        await ExistsInAuthAsync("", "Chess").Run()
            .Should().Within(30.Seconds()).Be(true);
        await ReadNameFromAuthAsync("", "Chess").Run()
            .Should().Within(30.Seconds()).Be("Chess");

        await _fixture.StorageAdapter.Delete("Chess").Should().Within(30.Seconds()).Emit();

        await ExistsInAuthAsync("", "Chess").Run()
            .Should().Within(30.Seconds()).Be(false);
    }

    /// <summary>
    /// The auth mirror SELF-HEALS: <c>GetAuthMirrorSelfHealScript</c> (run by
    /// <c>InitializeAsync</c> step 5 on every boot) re-installs a dropped partition trigger and
    /// reconciles rows that were missed while the trigger/function was broken. Pins the durable
    /// fix for the 2026-07 "spaces invisible in the catalog" incident: damage the mirror both
    /// ways (drop the trigger, delete a mirrored row, write a row while the trigger is absent),
    /// heal, and assert everything converged.
    /// </summary>
    [Fact]
    public async Task SelfHeal_ReinstallsTriggerAndReconcilesRows()
    {
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        await EnsureMirrorInstalledAsync().Run().Should().Within(60.Seconds()).Emit();

        // A real partition schema (the heal sweep deliberately skips 'public').
        await using (var proc = _fixture.DataSource.CreateCommand(
            "SELECT public.ensure_partition_schema('healtest')"))
            await proc.ExecuteNonQueryAsync();

        // Baseline: a Space root in the partition mirrors into auth via the trigger.
        await using (var ins = _fixture.DataSource.CreateCommand(
            """INSERT INTO "healtest".mesh_nodes (namespace, id, name, node_type, state) VALUES ('', 'HealMe', 'Heal Me', 'Space', 2) ON CONFLICT (namespace, id) DO NOTHING"""))
            await ins.ExecuteNonQueryAsync();
        await ExistsInAuthAsync("", "HealMe").Run().Should().Within(30.Seconds()).Be(true);

        // Damage 1: the trigger disappears (bad provisioning window).
        await using (var drop = _fixture.DataSource.CreateCommand(
            """DROP TRIGGER IF EXISTS mesh_node_mirror_access_objects ON "healtest".mesh_nodes"""))
            await drop.ExecuteNonQueryAsync();
        // Damage 2: an already-mirrored row is lost from auth.
        await using (var del = _fixture.DataSource.CreateCommand(
            """DELETE FROM "auth".mesh_nodes WHERE namespace = '' AND id = 'HealMe'"""))
            await del.ExecuteNonQueryAsync();
        // Damage 3: a row written while the trigger is absent never mirrors.
        await using (var ins2 = _fixture.DataSource.CreateCommand(
            """INSERT INTO "healtest".mesh_nodes (namespace, id, name, node_type, state) VALUES ('', 'HealMe2', 'Heal Me Too', 'Space', 2) ON CONFLICT (namespace, id) DO NOTHING"""))
            await ins2.ExecuteNonQueryAsync();
        await ExistsInAuthAsync("", "HealMe2").Run().Should().Within(30.Seconds()).Be(false);

        // Heal — the exact script InitializeAsync runs at every boot.
        await using (var heal = _fixture.DataSource.CreateCommand(
            PostgreSqlSchemaInitializer.GetAuthMirrorSelfHealScript()))
            await heal.ExecuteNonQueryAsync();

        // Both rows reconciled…
        await ExistsInAuthAsync("", "HealMe").Run().Should().Within(30.Seconds()).Be(true);
        await ExistsInAuthAsync("", "HealMe2").Run().Should().Within(30.Seconds()).Be(true);

        // …and the trigger is back: a fresh write mirrors again without another heal.
        await using (var ins3 = _fixture.DataSource.CreateCommand(
            """INSERT INTO "healtest".mesh_nodes (namespace, id, name, node_type, state) VALUES ('', 'HealMe3', 'Healed Live', 'Space', 2) ON CONFLICT (namespace, id) DO NOTHING"""))
            await ins3.ExecuteNonQueryAsync();
        await ExistsInAuthAsync("", "HealMe3").Run().Should().Within(30.Seconds()).Be(true);
    }

    [Fact]
    public async Task NonAuthNodeType_IsNotMirrored()
    {
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        await EnsureMirrorInstalledAsync().Run().Should().Within(60.Seconds()).Emit();

        var story = new MeshNode("story1", "ACME/Project")
        {
            Name = "Story One",
            NodeType = "Story"
        };
        await _fixture.StorageAdapter.Write(story, new()).Should().Within(30.Seconds()).Emit();

        await ExistsInAuthAsync("ACME/Project", "story1").Run()
            .Should().Within(30.Seconds()).Be(false);
    }
}
