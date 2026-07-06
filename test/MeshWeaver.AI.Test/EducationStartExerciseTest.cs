using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The edu module's resolve-or-copy + reader-shell layout areas
/// (<see cref="EducationLayoutAreas.StartExercise"/> / <see cref="EducationLayoutAreas.EnsurePersonalCopy"/>,
/// <see cref="EducationLayoutAreas.GoToMyCopy"/>, <see cref="EducationLayoutAreas.CourseNav"/> /
/// <see cref="EducationLayoutAreas.Learn"/>).
///
/// <para>Real RLS (<see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — NO <c>PublicAdminAccess</c>) so a
/// learner is a genuine read-only user: a public <b>Viewer</b> grant on the course lets them SEE it, but
/// they lack Update, so the resolve-or-copy gives them a personal, writable copy under their own partition
/// instead of the shared template.</para>
///
/// <list type="bullet">
///   <item>StartExercise (auto-redirect) via <see cref="MeshOperations.RenderArea"/> under the learner:
///   redirect into the learner's OWN copy's <c>Learn</c> shell, the module subtree copied under the
///   learner, and a second visit idempotent (no re-copy).</item>
///   <item>The embeddable <see cref="EducationLayoutAreas.GoToMyCopy"/> button renders a "Go to Exercise"
///   button for a read-only learner (the click's resolve-or-copy logic, <see cref="EducationLayoutAreas.EnsurePersonalCopy"/>,
///   creates the copy once and is idempotent thereafter).</item>
///   <item><see cref="EducationLayoutAreas.CourseNav"/> lists the containing space's child pages ordered by
///   <c>Order</c>, and the <see cref="EducationLayoutAreas.Learn"/> reader shell's nav pane is
///   collapsible.</item>
/// </list>
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
                // A minimal course: landing → module → two pages. Ex1 ("Your Turn") is given a LOWER Order
                // than Solutions even though its name sorts AFTER — so the CourseNav ordering test can prove
                // the nav follows Order, not the alphabet.
                new MeshNode("TestCourse")
                { Name = "Test Course", NodeType = "Markdown", Content = new MarkdownContent { Content = "# Test Course" } },
                new MeshNode("Module1", "TestCourse")
                { Name = "Module 1", NodeType = "Markdown", Content = new MarkdownContent { Content = "# Module 1" } },
                new MeshNode("Ex1", "TestCourse/Module1")
                { Name = "Your Turn", NodeType = "Markdown", Order = 1, Content = new MarkdownContent { Content = "# Exercises" } },
                new MeshNode("Solutions", "TestCourse/Module1")
                { Name = "Solutions", NodeType = "Markdown", Order = 2, Content = new MarkdownContent { Content = "# Solutions" } }
            );

    // The layout-client config so GetClient().GetWorkspace() + GetControlStream can render an area to a
    // typed UiControl (the pattern VersionViewsTest uses).
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

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

    [Fact(Timeout = 120_000)]
    public async Task GoToMyCopy_RendersButton_ForReadOnlyViewer()
    {
        // The embeddable "Go to Exercise" affordance (@@("area/GoToMyCopy")) renders a clickable BUTTON —
        // NOT an auto-redirect. A read-only viewer (the auto-login user holds only the public Viewer grant
        // here) is offered the copy-to-home button; the copy itself runs on click, so rendering the area
        // has no side effect.
        var control = await RenderControl(EducationLayoutAreas.GoToMyCopyArea);

        var button = control.Should().BeOfType<ButtonControl>(
            "GoToMyCopy renders a button a markdown page can embed — an embedded RedirectControl would " +
            "never drive top-level navigation").Subject;
        button.Data?.ToString().Should().Contain("Go to Exercise");
    }

    [Fact(Timeout = 120_000)]
    public async Task GoToExercise_EnsurePersonalCopy_CopiesOnce_AndIsIdempotent()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var learner = new AccessContext { ObjectId = LearnerId, Name = "Learner" };
        access.SetContext(learner);
        access.SetCircuitContext(learner);
        try
        {
            var landingExpected = $"{LearnerId}/TestCourse/Module1/Ex1";

            // The click logic behind the GoToMyCopy button: resolve-or-copy the module into the learner's
            // home and return the copy's landing path. Cold + reactive — the copy runs on Subscribe.
            Task<string> Ensure()
            {
                using (access.SwitchAccessContext(learner))
                    return EducationLayoutAreas.EnsurePersonalCopy(Mesh, "TestCourse/Module1/Ex1")
                        .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).ToTask();
            }

            // 1. First press: copies the module subtree under the learner and resolves to the copy.
            var landing1 = await Ensure();
            landing1.Should().Be(landingExpected, "the learner is routed into THEIR OWN copy, not the template");

            var copied = await PollUntilPaths(
                $"path:{LearnerId}/TestCourse/Module1 scope:subtree is:main",
                paths => paths.Contains($"{LearnerId}/TestCourse/Module1/Ex1")
                         && paths.Contains($"{LearnerId}/TestCourse/Module1/Solutions"));
            copied.Should().Contain($"{LearnerId}/TestCourse/Module1/Ex1");
            copied.Should().Contain($"{LearnerId}/TestCourse/Module1/Solutions");
            var copiedCount = copied.Count;

            // 2. Second press: idempotent — resolves to the SAME copy without duplicating anything.
            var landing2 = await Ensure();
            landing2.Should().Be(landingExpected);

            var afterSecond = await PollUntilPaths(
                $"path:{LearnerId}/TestCourse/Module1 scope:subtree is:main", _ => true);
            afterSecond.Count.Should().Be(copiedCount,
                "resolve-or-copy is idempotent — the second press navigates to the existing copy, not a re-copy");
        }
        finally
        {
            access.SetCircuitContext(null);
            access.SetContext(null);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_ListsSpaceChildPages_OrderedByOrderThenName()
    {
        // The nav is sourced from the containing space's DIRECT children and ordered by Order then Name —
        // the same pure function the side-nav renders with, fed the REAL query result so the ordering is
        // pinned without reaching through the render/serialization layer (a keyed EntityStore does not
        // preserve nav-link sequence for a string scan).
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var change = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery("path:TestCourse/Module1 scope:subtree is:main"))
            .Where(c => c.ChangeType == QueryChangeType.Initial).Take(1)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var pages = EducationLayoutAreas.SelectCoursePages("TestCourse/Module1", change.Items);

        // Only the module's direct-child PAGES (the module root itself is excluded), in Order sequence:
        // Ex1 ("Your Turn", Order 1) BEFORE Solutions (Order 2) — the REVERSE of alphabetical, proving the
        // nav follows Order, not the name.
        pages.Select(p => p.Path).Should().Equal(
            "TestCourse/Module1/Ex1",
            "TestCourse/Module1/Solutions");
        pages.Select(p => p.Name).Should().Equal("Your Turn", "Solutions");
    }

    [Fact(Timeout = 120_000)]
    public async Task Learn_ReaderShell_IsSplitter_WithCollapsibleNavPane()
    {
        // The Learn reader shell is a Splitter whose nav (left) pane is collapsible — the one-line way to
        // enable the "collapsible course side-nav" (link a page to /Learn).
        var control = await RenderControl(EducationLayoutAreas.LearnArea);

        var splitter = control.Should().BeOfType<SplitterControl>(
            "the Learn reader shell is a Splitter — nav pane + page content").Subject;
        var navPane = splitter.Areas.First();
        var paneSkin = navPane.Skins.OfType<SplitterPaneSkin>().FirstOrDefault();
        paneSkin.Should().NotBeNull("the nav pane carries a SplitterPaneSkin");
        paneSkin!.Collapsible.Should().Be(true, "the course side-nav pane is collapsible");
    }

    // Renders one area on the TestCourse/Module1/Ex1 node through the layout client (GetControlStream), so
    // the assertion inspects the real deserialized UiControl the browser would receive.
    private async Task<UiControl?> RenderControl(string area)
    {
        var client = GetClient();
        var nodeAddress = new Address("TestCourse/Module1/Ex1");
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var reference = new LayoutAreaReference(area);
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);
        return await stream.GetControlStream(reference.Area!)
            .Where(c => c is not null)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
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
