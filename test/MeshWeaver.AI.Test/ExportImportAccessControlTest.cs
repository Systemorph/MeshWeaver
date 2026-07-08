#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Access-control matrix for <see cref="MeshOperations.Export"/> / <see cref="MeshOperations.Import"/>.
/// Export is its OWN permission (<see cref="Permission.Export"/>), which the built-in Editor and Admin
/// roles grant but Viewer/Commenter do NOT. This fixture enforces real row-level security
/// (<see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — no <c>PublicAdminAccess</c>) and seeds a
/// source subtree plus per-user role grants, then drives Export/Import as different users:
/// <list type="bullet">
///   <item>a reader WITHOUT Export (Viewer) → empty export (proves Export ≠ Read);</item>
///   <item>a user with no grants at all → empty export;</item>
///   <item>PARTIAL Export (Editor on one child only) → export contains ONLY the permitted node;</item>
///   <item>FULL Export (Editor on the root, inherited) → the whole subtree;</item>
///   <item>Import as a user without Create on the target → denied, zero nodes written.</item>
/// </list>
///
/// <para>Note on <see cref="Permission.Export"/>: it exists in the permission model already and is
/// part of <see cref="Permission.All"/> (Admin) and of the built-in <see cref="Role.Editor"/> grant —
/// verified, not added.</para>
/// </summary>
public class ExportImportAccessControlTest : MonolithMeshTestBase
{
    private static readonly string ContentBasePath = Path.Combine(
        Path.GetTempPath(), "ExportImportAccessTest_" + Guid.NewGuid().ToString("N"));

    private const string SrcRoot = "ExpSrc";
    private const string EditorUser = "editor-user";
    private const string ViewerUser = "viewer-user";
    private const string PartialUser = "partial-user";
    private const string Stranger = "stranger-user";

    public ExportImportAccessControlTest(ITestOutputHelper output) : base(output) { }

    // Real RLS (ConfigureMeshBase, no PublicAdminAccess). Seed the source subtree (static config nodes,
    // present at init) + a content collection so the export's collection-config lookup responds, and
    // per-user role grants as AccessAssignment nodes.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder)
            .AddAI()
            .ConfigureDefaultNodeHub(config =>
            {
                var flat = config.Address.ToString().Replace('/', '_');
                var dir = Path.Combine(ContentBasePath, flat);
                var content = new ContentCollectionConfig
                {
                    Name = "content",
                    SourceType = "FileSystem",
                    IsEditable = true,
                    ExposeInChildren = true,
                    BasePath = dir,
                    Settings = new Dictionary<string, string> { ["BasePath"] = dir },
                };
                return config.AddContentCollection(_ => content);
            })
            .AddMeshNodes(
                new MeshNode(SrcRoot) { Name = "Export Source", NodeType = "Markdown" },
                new MeshNode("DocA", SrcRoot) { Name = "Doc A", NodeType = "Markdown" },
                new MeshNode("DocB", SrcRoot) { Name = "Doc B", NodeType = "Markdown" },
                // Grants: Editor on the whole subtree; Viewer (Read, NO Export) on the subtree;
                // Editor on ONLY DocA (partial). Inherited downward, never upward.
                AssignmentNodeFactory.UserRole(EditorUser, Role.Editor.Id, SrcRoot),
                AssignmentNodeFactory.UserRole(ViewerUser, Role.Viewer.Id, SrcRoot),
                AssignmentNodeFactory.UserRole(PartialUser, Role.Editor.Id, $"{SrcRoot}/DocA"));

    private MeshOperations Ops() => new(Mesh);

    private void SetUser(string userId)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var ctx = new AccessContext { ObjectId = userId, Name = userId };
        access.SetContext(ctx);
        access.SetCircuitContext(ctx);
    }

    private MeshExportManifest ReadManifest(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var r = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        return JsonSerializer.Deserialize<MeshExportManifest>(r.ReadToEnd(), Mesh.JsonSerializerOptions)!;
    }

    // (a) Reader WITHOUT the Export permission (Viewer) — export must be empty.
    [Fact(Timeout = 90000)]
    public async Task Export_ReaderWithoutExportPermission_ReturnsEmpty()
    {
        SetUser(ViewerUser);
        var zip = await Ops().Export(SrcRoot).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        ReadManifest(zip).Nodes.Should()
            .BeEmpty("Viewer grants Read but NOT the Export permission — nothing may be exported");
    }

    // (a) No access at all — export must be empty.
    [Fact(Timeout = 90000)]
    public async Task Export_NoAccessAtAll_ReturnsEmpty()
    {
        SetUser(Stranger);
        var zip = await Ops().Export(SrcRoot).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        ReadManifest(zip).Nodes.Should().BeEmpty("a user with no grants exports nothing");
    }

    // (b) PARTIAL access — Editor on ExpSrc/DocA only — export contains ONLY DocA.
    [Fact(Timeout = 90000)]
    public async Task Export_PartialAccess_ExportsOnlyPermittedSubset()
    {
        SetUser(PartialUser);
        var zip = await Ops().Export(SrcRoot).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        var paths = ReadManifest(zip).Nodes.Select(n => n.Path).ToList();

        paths.Should().Contain($"{SrcRoot}/DocA", "the caller has Export on DocA");
        paths.Should().NotContain(SrcRoot, "no Export on the root — it must be silently skipped");
        paths.Should().NotContain($"{SrcRoot}/DocB", "no Export on the sibling — it must not leak");
    }

    // (c) FULL access — Editor on ExpSrc (inherited) — the whole subtree.
    [Fact(Timeout = 90000)]
    public async Task Export_EditorFullAccess_ExportsWholeSubtree()
    {
        SetUser(EditorUser);
        var zip = await Ops().Export(SrcRoot).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        var paths = ReadManifest(zip).Nodes.Select(n => n.Path).ToList();

        paths.Should().Contain(new[] { SrcRoot, $"{SrcRoot}/DocA", $"{SrcRoot}/DocB" },
            "Editor on the root grants Export across the whole subtree");
    }

    // (d) Import as a user without Create on the target — denied, zero nodes written.
    [Fact(Timeout = 90000)]
    public async Task Import_WithoutCreatePermission_DeniedNoPartialWrite()
    {
        // Produce a full export as the Editor.
        SetUser(EditorUser);
        var zip = await Ops().Export(SrcRoot).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        ReadManifest(zip).Nodes.Should().NotBeEmpty("the editor produced a non-empty export to import");

        // Import as a stranger with no Create on the target namespace — every CreateNode is denied.
        const string target = "ImpDst";
        SetUser(Stranger);
        var result = await Ops().Import(target, zip).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        Output.WriteLine(result);

        var doc = JsonDocument.Parse(result).RootElement;
        doc.GetProperty("nodesImported").GetInt32().Should()
            .Be(0, "no Create permission on the target → no node may be written (no partial import)");
        doc.GetProperty("filesImported").GetInt32().Should().Be(0);
    }
}
