using System;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Feedback NodeType is wired end-to-end: a node created with <c>nodeType = "Feedback"</c> and a
/// typed <see cref="Feedback"/> content reads back with its content STILL typed as <see cref="Feedback"/>
/// — proving <c>AddFeedbackType</c>'s data source plus the central <c>WithType</c> registration. Every
/// field the <c>/feedback</c> skill sets (the text, the captured <see cref="Feedback.Location"/>, the
/// submitter, the <see cref="FeedbackStatus"/>) round-trips.
/// </summary>
public class FeedbackNodeTypeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 60000)]
    public async Task FeedbackNode_RoundTrips_Typed()
    {
        // Filed under the submitter's OWN partition — {user}/Feedback/{id} — the private, self-scoped home.
        await MeshService.CreateNode(new MeshNode("search-is-slow", "rbuergi/Feedback")
        {
            NodeType = FeedbackNodeType.NodeType,
            Name = "Search is slow",
            Icon = "📣",
            Content = new Feedback
            {
                Text = "The search box takes ages on big spaces.",
                Location = "ACME/Reports/Q3",
                SubmittedBy = "rbuergi",
                SubmittedByName = "Roland Bürgi",
                Category = "bug",
                Status = FeedbackStatus.New,
            },
        }).Should().Emit();

        var node = await ReadNode("rbuergi/Feedback/search-is-slow")
            .Where(n => n?.Content is Feedback)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30));

        node!.NodeType.Should().Be(FeedbackNodeType.NodeType);
        var feedback = (Feedback)node.Content!;
        feedback.Text.Should().Be("The search box takes ages on big spaces.");
        feedback.Location.Should().Be("ACME/Reports/Q3");   // the captured app-context path
        feedback.SubmittedBy.Should().Be("rbuergi");
        feedback.SubmittedByName.Should().Be("Roland Bürgi");
        feedback.Category.Should().Be("bug");
        feedback.Status.Should().Be(FeedbackStatus.New);
    }
}
