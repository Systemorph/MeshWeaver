using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A NodeType DECLARATION declares itself a NodeType — never the type it declares.
///
/// <para><c>UserNodeType.CreateMeshNode()</c> shipped the <c>User</c> declaration with
/// <c>NodeType = "User"</c>, i.e. the node that DEFINES the type also claimed to BE one. There is
/// no way to tell it apart from a real account after that: it is returned by every
/// <c>nodeType:User</c> query in the mesh, and it carries a <see cref="NodeTypeDefinition"/> where
/// each consumer expects a <see cref="User"/>. The portal's user DIRECTORY
/// (<see cref="UserIdentityCache"/> — email → mesh user) is the loudest victim: it read the
/// declaration on EVERY index snapshot and logged
/// <c>As&lt;User&gt; for User: value is NodeTypeDefinition</c> each time, 355k+ occurrences in
/// production (Systemorph/MeshWeaver#2160/#2161/#2162). The same collision made AI-source installs
/// resolve a partition literally named "User" (<c>42P01: relation "user.mesh_nodes" does not
/// exist</c>) and left a stray <c>vuser</c> partition behind — which is why
/// <c>AiSourcesInstallHook</c> carried a hand-rolled <c>id != "User"</c> filter until this landed.
/// A filter names two types; the invariant covers all of them, which is what this class pins.</para>
/// </summary>
public class NodeTypeDeclarationSelfTypingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// The ratchet. Every statically-registered declaration — anything whose content IS a
    /// <see cref="NodeTypeDefinition"/> — must say so in its <see cref="MeshNode.NodeType"/>:
    /// either unset, or the literal <see cref="MeshNode.NodeTypePath"/>. A declaration that names
    /// the type it declares is the defect, and it is invisible to every other gate — the node
    /// builds, activates, and renders; it just quietly joins its own instances in every query.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void NoDeclaration_ClaimsToBeAnInstanceOfTheTypeItDeclares()
    {
        var offenders = Mesh.ServiceProvider.EnumerateStaticNodes()
            .Where(n => n.Content is NodeTypeDefinition)
            .Where(n => !string.IsNullOrEmpty(n.NodeType)
                && !string.Equals(n.NodeType, MeshNode.NodeTypePath, StringComparison.OrdinalIgnoreCase))
            .Select(n => $"{n.Path} (nodeType:{n.NodeType})")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        foreach (var offender in offenders)
            Output.WriteLine($"self-typed declaration: {offender}");

        offenders.Should().BeEmpty(
            "a NodeType declaration must carry NodeType = \"{0}\" (or leave it unset), never the "
            + "name of the type it declares — otherwise it is returned by every nodeType:<Type> "
            + "query alongside the real instances, carrying NodeTypeDefinition content where the "
            + "caller expects the instance type",
            MeshNode.NodeTypePath);
    }

    /// <summary>
    /// The call site the incident was reported from, end to end: the portal's user directory query
    /// (<see cref="UserIdentityCache.DirectoryQuery"/> — verbatim, so a change to it re-aims this
    /// test automatically) must return USERS. Every row it hands back is read as a
    /// <see cref="User"/> by <c>UserIdentityCache.TryGetEmail</c>, so a row that is not one is not
    /// a longer list — it is an error line per row per snapshot, forever.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheUserDirectory_ReturnsUsers_NotTheUserDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            await MeshService.CreateNode(MeshNode.FromPath("directoryprobe") with
            {
                Name = "Directory Probe",
                NodeType = UserNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new User { Email = "directoryprobe@meshweaver.io" }
            }).Should().Within(60.Seconds()).Emit();
        }

        var rows = await MeshService.Query<MeshNode>(UserIdentityCache.DirectoryQuery)
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Select(c => c.Items)
            .Where(items => items.Any(n => n.Path == "directoryprobe"))
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask(ct);

        var notUsers = new List<string>();
        foreach (var row in rows)
        {
            var actual = row.Content?.GetType().Name ?? "(null)";
            Output.WriteLine($"directory row: {row.Path} nodeType={row.NodeType} content={actual}");
            // ContentAs, not `is User` — content legitimately arrives typed, as a JsonElement or as
            // the as-written DOM, and only the first would satisfy a CLR type test
            // (UserDirectoryContentShapeTest pins all three). A row that cannot be read as a User
            // is the failure this test is about.
            if (row.ContentAs<User>(Mesh.JsonSerializerOptions) is null)
                notUsers.Add($"{row.Path} (nodeType:{row.NodeType}, content:{actual})");
        }

        notUsers.Should().BeEmpty(
            "every row of the portal's user directory is read as a User by UserIdentityCache — the "
            + "`User` NodeType declaration used to be in here because it declared itself "
            + "nodeType:User, and it produced one `As<User> for User: value is NodeTypeDefinition` "
            + "error per row per index snapshot");
    }
}
