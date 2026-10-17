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
///   earlier — so a field another writer changed in between is not clobbered. (For the Hub
///   Configuration editor, whose Save button this file used to pin, that is now a property of the
///   per-field node binding: <c>NodeTypeAndSettingsPagesAreTemplatesTest</c>.)</item>
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
