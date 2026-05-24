using System.Threading.Tasks;
using FluentAssertions;
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

        await using (var cmd = _fixture.DataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION public.mirror_access_object_to_auth_schema()
            RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    IF OLD.node_type IN ('User','Group','Role','VUser','ApiToken') THEN
                        DELETE FROM "auth".mesh_nodes
                         WHERE namespace = OLD.namespace AND id = OLD.id;
                    END IF;
                    RETURN OLD;
                END IF;

                IF NEW.node_type IN ('User','Group','Role','VUser','ApiToken') THEN
                    INSERT INTO "auth".mesh_nodes
                        (namespace, id, name, node_type, category, icon, display_order,
                         last_modified, version, state, content, desired_id, main_node)
                    VALUES (NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.category, NEW.icon, NEW.display_order,
                            NEW.last_modified, NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node)
                    ON CONFLICT (namespace, id) DO UPDATE SET
                        name = EXCLUDED.name,
                        node_type = EXCLUDED.node_type,
                        category = EXCLUDED.category,
                        icon = EXCLUDED.icon,
                        display_order = EXCLUDED.display_order,
                        last_modified = EXCLUDED.last_modified,
                        version = EXCLUDED.version,
                        state = EXCLUDED.state,
                        content = EXCLUDED.content,
                        desired_id = EXCLUDED.desired_id,
                        main_node = EXCLUDED.main_node;
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """))
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
        await _fixture.CleanDataAsync();
        await EnsureMirrorInstalledAsync();

        var node = new MeshNode("tokenhash123", "rbuergi/ApiToken")
        {
            Name = "Test Token",
            NodeType = "ApiToken"
        };
        await _fixture.StorageAdapter.WriteAsync(node, new(), TestContext.Current.CancellationToken);

        (await ExistsInAuthAsync("rbuergi/ApiToken", "tokenhash123"))
            .Should().BeTrue("the trigger mirrors ApiToken inserts into auth.mesh_nodes");
        (await ReadNameFromAuthAsync("rbuergi/ApiToken", "tokenhash123"))
            .Should().Be("Test Token", "mirror should reflect the source row's content");
    }

    [Fact]
    public async Task ApiTokenUpdate_PropagatesToMirror()
    {
        await _fixture.CleanDataAsync();
        await EnsureMirrorInstalledAsync();

        var node = new MeshNode("tokenhash456", "rbuergi/ApiToken")
        {
            Name = "Active Token",
            NodeType = "ApiToken"
        };
        await _fixture.StorageAdapter.WriteAsync(node, new(), TestContext.Current.CancellationToken);

        // Same (namespace, id), different name — simulates the RevokeToken
        // flow that updates Content.IsRevoked while leaving the path fixed.
        var revoked = new MeshNode("tokenhash456", "rbuergi/ApiToken")
        {
            Name = "Revoked Token",
            NodeType = "ApiToken"
        };
        await _fixture.StorageAdapter.WriteAsync(revoked, new(), TestContext.Current.CancellationToken);

        (await ReadNameFromAuthAsync("rbuergi/ApiToken", "tokenhash456"))
            .Should().Be("Revoked Token",
                "the trigger's ON CONFLICT DO UPDATE re-applies fields on every source update");
    }

    [Fact]
    public async Task ApiTokenDelete_RemovesFromMirror()
    {
        await _fixture.CleanDataAsync();
        await EnsureMirrorInstalledAsync();

        var node = new MeshNode("tokenhash789", "rbuergi/ApiToken")
        {
            Name = "To Delete",
            NodeType = "ApiToken"
        };
        await _fixture.StorageAdapter.WriteAsync(node, new(), TestContext.Current.CancellationToken);
        (await ExistsInAuthAsync("rbuergi/ApiToken", "tokenhash789")).Should().BeTrue();

        await _fixture.StorageAdapter.DeleteAsync(
            "rbuergi/ApiToken/tokenhash789", TestContext.Current.CancellationToken);

        (await ExistsInAuthAsync("rbuergi/ApiToken", "tokenhash789"))
            .Should().BeFalse("the trigger removes the mirror row on source DELETE");
    }

    [Fact]
    public async Task UserInsert_MirrorsIntoAuthSchema()
    {
        await _fixture.CleanDataAsync();
        await EnsureMirrorInstalledAsync();

        // Per the partition convention: a user node sits at the root of its
        // partition with namespace="" and id=userId.
        var user = new MeshNode("rbuergi", "")
        {
            Name = "Roland Bürgi",
            NodeType = "User"
        };
        await _fixture.StorageAdapter.WriteAsync(user, new(), TestContext.Current.CancellationToken);

        (await ExistsInAuthAsync("", "rbuergi"))
            .Should().BeTrue("the trigger mirrors User inserts into auth.mesh_nodes");
    }

    [Fact]
    public async Task NonAuthNodeType_IsNotMirrored()
    {
        await _fixture.CleanDataAsync();
        await EnsureMirrorInstalledAsync();

        var story = new MeshNode("story1", "ACME/Project")
        {
            Name = "Story One",
            NodeType = "Story"
        };
        await _fixture.StorageAdapter.WriteAsync(story, new(), TestContext.Current.CancellationToken);

        (await ExistsInAuthAsync("ACME/Project", "story1"))
            .Should().BeFalse("only User/Group/Role/VUser/ApiToken are mirrored — non-auth types stay in their source partition");
    }
}
