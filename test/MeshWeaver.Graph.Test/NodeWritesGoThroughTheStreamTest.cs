using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Node writes that used to be a bespoke <c>DataChangeRequest</c> — a one-shot read of the node,
/// then the WHOLE node posted back — now go through <c>GetMeshNodeStream(path).Update(…)</c>
/// (Doc/Architecture/DataPlaneMessagesAreStreamPlumbing).
///
/// <para>Two properties are pinned, and both are what the bespoke path could not give:</para>
/// <list type="number">
///   <item><b>The write is the CALLER's.</b> The stream write stamps <c>LastModifiedBy</c> with the
///   authenticated identity captured at the call; the bespoke post carried a node copy whose
///   authorship was whatever the read returned.</item>
///   <item><b>Unedited fields are carried from the node's CURRENT state</b>, never from a copy taken
///   earlier — so a field another writer changed in between is not clobbered.</item>
/// </list>
/// </summary>
public class NodeWritesGoThroughTheStreamTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static AccessContext IdentityFor(string userPath) =>
        new() { ObjectId = userPath, Name = userPath, Email = $"{userPath}@meshweaver.io" };

    /// <summary>
    /// <c>ClearUserBody</c> — behind the home's "Reset to default" and the editor's clear action —
    /// clears the Body as the user who asked, and a view already bound to the node sees it LIVE.
    /// The subscriber is opened BEFORE the write and never re-subscribed, so this is the binding a
    /// rendered home holds, not a fresh read.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ClearingTheHomeBody_IsWrittenAsTheCaller_AndReachesALiveBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        const string user = "bodyowner";
        await CreateUserAsync(user, new User { Body = "# My own home" }, ct);

        // The live binding: subscribed once, before the write, kept open.
        var cleared = new AsyncSubject<MeshNode>();
        using var binding = Mesh.GetWorkspace().GetMeshNodeStream(user)
            .Where(n => n?.ContentAs<User>(Mesh.JsonSerializerOptions) is { Body: null })
            .Take(1)
            .Subscribe(cleared);

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAs(IdentityFor(user), () =>
                UserActivityLayoutAreas.ClearUserBody(Mesh.GetWorkspace(), user, Mesh.JsonSerializerOptions))
            .Should().Within(20.Seconds()).Emit("the write through the node stream acknowledges", cancellationToken: ct);

        var node = await cleared.Should().Within(20.Seconds())
            .Emit("a view bound to the user node sees the cleared Body without re-reading", cancellationToken: ct);

        node.LastModifiedBy.Should().Be(user,
            "the write is attributed to the user who asked — the stream write stamps the caller's "
            + "authenticated identity; a node copy posted back as a DataChangeRequest carries whatever "
            + "authorship the earlier read returned");
    }

    /// <summary>
    /// The Hub Configuration editor's Save applies the form to the node's CURRENT state. A field the
    /// form does not edit — here the compile status another writer set after the form rendered —
    /// survives the save.
    /// </summary>
    [Fact]
    public void HubConfigSave_CarriesUneditedFieldsFromTheCurrentNode()
    {
        var current = new MeshNode("Widget", "Acme")
        {
            NodeType = MeshNode.NodeTypePath,
            Name = "Widget",
            Category = "Tools",
            Content = new NodeTypeDefinition
            {
                Description = "old",
                CompilationStatus = CompilationStatus.Error,
                CompilationError = "CS0103 set by the compiler after the form rendered",
            },
        };

        var form = new NodeTypeLayoutAreas.HubConfigForm(
            DisplayName: "Widget 2",
            Description: "new",
            IconName: "",
            Order: "3",
            ChildrenQuery: "",
            Dependencies: "Acme/Base, Acme/Other",
            Configuration: "config => config");

        var saved = NodeTypeLayoutAreas.ApplyHubConfigForm(
            current, current.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions), form);
        var definition = saved.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;

        saved.Name.Should().Be("Widget 2");
        saved.Order.Should().Be(3);
        saved.Icon.Should().BeNull("a blank form field clears the value");
        definition.Description.Should().Be("new");
        definition.Dependencies.Should().Equal("Acme/Base", "Acme/Other");

        saved.Category.Should().Be("Tools", "a node field the form does not edit is carried through");
        definition.CompilationStatus.Should().Be(CompilationStatus.Error,
            "a definition field the form does not edit is carried from the CURRENT node, never "
            + "from a copy taken when the form rendered");
        definition.CompilationError.Should().Be("CS0103 set by the compiler after the form rendered");
    }

    private async Task CreateUserAsync(string path, User content, CancellationToken cancellationToken)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
        {
            NodeType = "User",
            Name = path,
            State = MeshNodeState.Active,
            Content = content,
        })).Should().Within(20.Seconds()).Emit("the user node is created", cancellationToken: cancellationToken);
    }
}
