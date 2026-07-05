using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The edu module's <see cref="EducationLayoutAreas.StartExercise"/> "Your Turn" resolve-or-copy flow.
///
/// <para>Real RLS (<see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — NO <c>PublicAdminAccess</c>) so a
/// learner is a genuine read-only user: a public <b>Viewer</b> grant on the course lets them SEE it, but
/// they lack Update, so StartExercise gives them a personal, writable copy under their own partition
/// instead of the shared template. Drives the production area through
/// <see cref="MeshOperations.RenderArea"/> (which waits for the area to materialize — so the async copy
/// completes) under the learner's identity, and asserts: (1) redirect into the learner's OWN copy's
/// <c>Learn</c> reader shell, (2) the module subtree was copied under the learner, (3) a second visit is
/// idempotent (no re-copy).</para>
/// </summary>
public class EducationStartExerciseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string LearnerId = "learner-user";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)               // real RLS — no blanket admin grant; already AddGraph()s
            .AddMeshNodes(
                // Public VIEWER (read-only) on the course: a learner can view + copy it, but NOT edit it.
                new MeshNode(WellKnownUsers.Public + "_Access", "TestCourse/_Access")
                {
                    NodeType = "AccessAssignment",
                    Name = "Public Read",
                    MainNode = "TestCourse",
                    Content = new AccessAssignment
                    {
                        AccessObject = WellKnownUsers.Public,
                        DisplayName = "Public",
                        Roles = [new RoleAssignment { Role = "Viewer" }]
                    }
                },
                // A minimal course: landing → module → two pages (Exercises + Solutions).
                new MeshNode("TestCourse")
                { Name = "Test Course", NodeType = "Markdown", Content = new MarkdownContent { Content = "# Test Course" } },
                new MeshNode("Module1", "TestCourse")
                { Name = "Module 1", NodeType = "Markdown", Content = new MarkdownContent { Content = "# Module 1" } },
                new MeshNode("Ex1", "TestCourse/Module1")
                { Name = "Your Turn", NodeType = "Markdown", Content = new MarkdownContent { Content = "# Exercises" } },
                new MeshNode("Solutions", "TestCourse/Module1")
                { Name = "Solutions", NodeType = "Markdown", Content = new MarkdownContent { Content = "# Solutions" } }
            );

    [Fact(Timeout = 120_000)]
    public async Task StartExercise_Learner_CopiesModuleToOwnNamespace_AndRedirectsToLearn()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var learner = new AccessContext { ObjectId = LearnerId, Name = "Learner" };
        access.SetContext(learner);
        access.SetCircuitContext(learner);
        try
        {
            var ops = new MeshOperations(Mesh);
            var learnHref = $"{LearnerId}/TestCourse/Module1/Ex1/{EducationLayoutAreas.LearnArea}";

            // Subscribe under a deterministic learner-identity scope: RenderArea captures the caller at
            // subscribe time, so this pins the area to the LEARNER rather than the residual auto-login admin.
            Task<string> RenderStart()
            {
                using (access.SwitchAccessContext(learner))
                    return ops
                        .RenderArea("@TestCourse/Module1/Ex1", EducationLayoutAreas.StartExerciseArea, timeoutSeconds: 60)
                        .FirstAsync().Timeout(TimeSpan.FromSeconds(90)).ToTask();
            }

            // 1. Learner opens "Your Turn": StartExercise copies the module and redirects into the copy.
            var payload = await RenderStart();

            payload.Should().NotStartWith("Error", "the learner has Viewer access and a writable home");
            payload.Should().Contain("Redirect", "StartExercise resolves to a RedirectControl");
            payload.Should().Contain(learnHref,
                "the learner is redirected into THEIR OWN copy's Learn reader shell, not the shared template");

            // 2. The whole module subtree now exists under the learner's partition (poll: the query index
            //    is eventually consistent right after the copy).
            var copied = await PollUntilPaths(
                $"path:{LearnerId}/TestCourse/Module1 scope:subtree is:main",
                paths => paths.Contains($"{LearnerId}/TestCourse/Module1/Ex1")
                         && paths.Contains($"{LearnerId}/TestCourse/Module1/Solutions"));
            copied.Should().Contain($"{LearnerId}/TestCourse/Module1");
            copied.Should().Contain($"{LearnerId}/TestCourse/Module1/Ex1");
            copied.Should().Contain($"{LearnerId}/TestCourse/Module1/Solutions");
            var copiedCount = copied.Count;

            // 3. Idempotent: a second visit redirects to the existing copy WITHOUT re-copying.
            var payload2 = await RenderStart();
            payload2.Should().Contain(learnHref);

            var afterSecond = await PollUntilPaths(
                $"path:{LearnerId}/TestCourse/Module1 scope:subtree is:main",
                _ => true);
            afterSecond.Count.Should().Be(copiedCount,
                "resolve-or-copy is idempotent — the second visit navigates to the existing copy, not a re-copy");
        }
        finally
        {
            access.SetCircuitContext(null);
            access.SetContext(null);
        }
    }

    // Re-queries until the predicate holds (or times out) — the sanctioned wait-on-condition pattern for
    // the eventually-consistent query index (no fixed Task.Delay).
    private Task<IReadOnlyList<string>> PollUntilPaths(string query, Func<IReadOnlyList<string>, bool> predicate)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Take(1)
                .Select(c => (IReadOnlyList<string>)c.Items.Select(n => n.Path).ToList()))
            .Where(predicate)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask();
    }
}
