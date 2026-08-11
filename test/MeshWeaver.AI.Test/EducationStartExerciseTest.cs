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
///   <item><see cref="EducationLayoutAreas.CourseNav"/> indexes the WHOLE course — every module as a group
///   ordered by <c>Order</c>, only the current one expanded, the current page active (also while the learner
///   reads their own copy), and each exercise resolving into <c>{viewer}/…</c> and NEVER into the central
///   template — and the <see cref="EducationLayoutAreas.Learn"/> reader shell's nav pane is
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
                // A minimal course: landing → TWO modules → pages. Ex1 ("Your Turn") is given a LOWER Order
                // than Solutions even though its name sorts AFTER — so the CourseNav ordering test can prove
                // the nav follows Order, not the alphabet. Module2 ("Advanced") sorts BEFORE "Module 1"
                // alphabetically but carries a higher Order, so the module ordering is pinned the same way.
                new MeshNode("TestCourse")
                { Name = "Test Course", NodeType = "Markdown", Content = new MarkdownContent { Content = "# Test Course" } },
                // A childless direct child of the course: a course-level PAGE, not a module.
                new MeshNode("Intro", "TestCourse")
                { Name = "Introduction", NodeType = "Markdown", Order = 0, Content = new MarkdownContent { Content = "# Intro" } },
                new MeshNode("Module1", "TestCourse")
                { Name = "Module 1", NodeType = "Markdown", Order = 1, Content = new MarkdownContent { Content = "# Module 1" } },
                new MeshNode("Ex1", "TestCourse/Module1")
                { Name = "Your Turn", NodeType = "Markdown", Order = 1, Content = new MarkdownContent { Content = "# Exercises" } },
                new MeshNode("Solutions", "TestCourse/Module1")
                { Name = "Solutions", NodeType = "Markdown", Order = 2, Content = new MarkdownContent { Content = "# Solutions" } },
                new MeshNode("Module2", "TestCourse")
                { Name = "Advanced", NodeType = "Markdown", Order = 2, Content = new MarkdownContent { Content = "# Advanced" } },
                new MeshNode("Theory", "TestCourse/Module2")
                { Name = "Theory", NodeType = "Markdown", Order = 1, Content = new MarkdownContent { Content = "# Theory" } },
                // The module's Exercise/ sub-namespace — the convention real courses use
                // ({course}/{module}/Exercise/{n}); its CHILDREN are the exercises.
                new MeshNode("Exercise", "TestCourse/Module2")
                { Name = "Exercises", NodeType = "Markdown", Order = 5, Content = new MarkdownContent { Content = "# Exercises" } },
                new MeshNode("Drill1", "TestCourse/Module2/Exercise")
                { Name = "Drill One", NodeType = "Markdown", Order = 1, Content = new MarkdownContent { Content = "# Drill 1" } },
                new MeshNode("Drill2", "TestCourse/Module2/Exercise")
                { Name = "Drill Two", NodeType = "Markdown", Order = 2, Content = new MarkdownContent { Content = "# Drill 2" } },
                // A page BELOW the deepest level the rail lists — the rail must still show the whole index
                // and fall back to the nearest listed entry (the exercise) for "where am I".
                new MeshNode("Step1", "TestCourse/Module2/Exercise/Drill1")
                { Name = "Step One", NodeType = "Markdown", Order = 1, Content = new MarkdownContent { Content = "# Step 1" } }
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
        access.SetHostIdentity(learner);
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
            access.SetHostIdentity(null);
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
        access.SetHostIdentity(learner);
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
            access.SetHostIdentity(null);
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

    [Fact]
    public void SelectCoursePages_FiltersInternalSatelliteNodes()
    {
        // The nav rail is learner-facing: internal / satellite nodes (any whose final path segment starts
        // with '_' — _GitSync, _Access, _Thread, _Activity, …) are plumbing and must NOT appear as pages.
        // A GitHubSyncConfig is dropped BY TYPE too — it is a real main node that `is:main` keeps, and the
        // exclusion lives here rather than as a `-nodeType:` query term (a negation-only term is not honoured
        // by every provider — on the in-memory one it empties the whole result and blanks the rail).
        const string moduleRoot = "TestCourse/Module1";
        var nodes = new[]
        {
            new MeshNode("Module1", "TestCourse") { Name = "Module 1" }, // the module root itself
            new MeshNode("Intro", moduleRoot) { Name = "Intro", Order = 1 },
            new MeshNode("_GitSync", moduleRoot) { Name = "GitHub Sync", Order = 2 }, // internal — filtered
            new MeshNode("TDD", moduleRoot) { Name = "TDD", Order = 3 },
            new MeshNode("_Access", moduleRoot) { Name = "Access", Order = 4 },        // satellite — filtered
            new MeshNode("Sync", moduleRoot)                                           // by TYPE — filtered
            { Name = "Sync", Order = 5, NodeType = "GitHubSyncConfig" },
        };

        var pages = EducationLayoutAreas.SelectCoursePages(moduleRoot, nodes);

        // Only the real pages survive, order (Order then Name) preserved; every '_'-prefixed node is gone.
        pages.Select(p => p.Path).Should().Equal(
            "TestCourse/Module1/Intro",
            "TestCourse/Module1/TDD");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_IndexesTheWholeCourse_EveryModuleInOrder()
    {
        // The rail is the COURSE index, not the current module: reading a page of Module 1 must still list
        // EVERY module. Modules come back in Order sequence — "Advanced" (Order 2) after "Module 1"
        // (Order 1), the REVERSE of alphabetical, proving the module list follows Order, not the name.
        var nodes = await QueryCourseSubtree();

        var model = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", "TestCourse/Module1/Ex1", LearnerId, nodes, NoCopies);

        model.CoursePath.Should().Be("TestCourse");
        model.Home.SourcePath.Should().Be("TestCourse");
        model.Home.Href.Should().Be($"TestCourse/{EducationLayoutAreas.LearnArea}");
        model.Modules.Select(m => m.Path).Should().Equal("TestCourse/Module1", "TestCourse/Module2");
        model.Modules.Select(m => m.Title).Should().Equal("Module 1", "Advanced");
        // A childless direct child of the course is a course-level PAGE, not an (empty) module group.
        model.Pages.Select(p => p.SourcePath).Should().Equal("TestCourse/Intro");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_CurrentModuleExpanded_OtherModulesCollapsed_AndCurrentPageActive()
    {
        // "Where am I": only the module being read is expanded (an 18-module course stays scannable) and the
        // page being read is the one active link.
        var nodes = await QueryCourseSubtree();

        var model = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", "TestCourse/Module1/Ex1", LearnerId, nodes, NoCopies);

        var module1 = model.Modules.Single(m => m.Path == "TestCourse/Module1");
        var module2 = model.Modules.Single(m => m.Path == "TestCourse/Module2");
        module1.Expanded.Should().BeTrue("the learner is reading a page of Module 1");
        module2.Expanded.Should().BeFalse("every other module stays collapsed");

        // Module home first, then its pages in Order sequence.
        module1.Links.Select(l => l.SourcePath).Should().Equal(
            "TestCourse/Module1", "TestCourse/Module1/Ex1", "TestCourse/Module1/Solutions");
        AllLinks(model).Where(l => l.IsActive).Select(l => l.SourcePath)
            .Should().Equal("TestCourse/Module1/Ex1");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_FromThePersonalCopy_StillIndexesTheCourse_AndKnowsWhereYouAre()
    {
        // Working inside your OWN copy ({viewer}/{course}/…) must not shrink the rail to your partition:
        // the index is still the shared course, and the page you are editing is still the active link.
        var nodes = await QueryCourseSubtree();
        const string inMyCopy = $"{LearnerId}/TestCourse/Module1/Ex1";

        EducationLayoutAreas.ResolveCourseRoot(inMyCopy, LearnerId).Should().Be("TestCourse");

        var model = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", inMyCopy, LearnerId, nodes, NoCopies);

        model.Modules.Select(m => m.Path).Should().Equal("TestCourse/Module1", "TestCourse/Module2");
        model.Modules.Single(m => m.Path == "TestCourse/Module1").Expanded.Should().BeTrue();
        AllLinks(model).Where(l => l.IsActive).Select(l => l.SourcePath)
            .Should().Equal("TestCourse/Module1/Ex1");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_ListsExercises_AndNeverLinksTheCourseTemplate()
    {
        // Exercises ({course}/{module}/Exercise/{n}) are listed under their module — but a learner must NEVER
        // be sent to the read-only template. With no copy yet the entry carries the resolve-or-copy source
        // (the click runs EnsurePersonalCopy, exactly like GoToMyCopy) and NO href at all.
        var nodes = await QueryCourseSubtree();

        var model = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", "TestCourse/Module2/Theory", LearnerId, nodes, NoCopies);

        var module2 = model.Modules.Single(m => m.Path == "TestCourse/Module2");
        // Pages first, then the exercises — and the Exercise/ FOLDER is replaced by its children, never
        // listed alongside them.
        module2.Links.Select(l => l.SourcePath).Should().Equal(
            "TestCourse/Module2",
            "TestCourse/Module2/Theory",
            "TestCourse/Module2/Exercise/Drill1",
            "TestCourse/Module2/Exercise/Drill2");
        module2.Links.Where(l => l.IsExercise).Select(l => l.Title).Should().Equal("Drill One", "Drill Two");

        var exercises = AllLinks(model).Where(l => l.IsExercise).ToList();
        exercises.Should().HaveCount(2);
        exercises.Should().OnlyContain(l => l.Href == null,
            "with no personal copy yet the entry resolves on click — it never gets a template href");
        exercises.Select(l => l.ResolveFromPath).Should().Equal(
            "TestCourse/Module2/Exercise/Drill1", "TestCourse/Module2/Exercise/Drill2");

        // THE invariant: no exercise link the rail emits points into the central course.
        AssertNoExerciseLinksTheTemplate(model, "TestCourse");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_ExerciseWithAPersonalCopy_LinksIntoThatCopy()
    {
        // Once the copy exists the entry becomes a real href — into {viewer}/…, still never the template.
        var nodes = await QueryCourseSubtree();
        var copies = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{LearnerId}/TestCourse/Module2/Exercise",
            $"{LearnerId}/TestCourse/Module2/Exercise/Drill1",
        };

        var model = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", "TestCourse/Module2/Theory", LearnerId, nodes, copies);

        var exercises = AllLinks(model).Where(l => l.IsExercise).ToList();
        var drill1 = exercises.Single(l => l.SourcePath == "TestCourse/Module2/Exercise/Drill1");
        drill1.Href.Should().Be($"{LearnerId}/TestCourse/Module2/Exercise/Drill1/{EducationLayoutAreas.LearnArea}");
        drill1.ResolveFromPath.Should().BeNull("the copy exists — nothing left to resolve");

        // The one WITHOUT a copy still resolves on click.
        var drill2 = exercises.Single(l => l.SourcePath == "TestCourse/Module2/Exercise/Drill2");
        drill2.Href.Should().BeNull();
        drill2.ResolveFromPath.Should().Be("TestCourse/Module2/Exercise/Drill2");

        AssertNoExerciseLinksTheTemplate(model, "TestCourse");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_AnonymousViewer_ListsExercises_WithoutLinkingTheTemplate()
    {
        // No writable home → no personal copy is possible. The exercise is still listed (the index stays
        // complete) but it is NOT navigable: linking the template is the one thing we never do.
        var nodes = await QueryCourseSubtree();

        var model = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", "TestCourse/Module2/Theory", viewer: null, nodes, NoCopies);

        var exercises = AllLinks(model).Where(l => l.IsExercise).ToList();
        exercises.Should().HaveCount(2);
        exercises.Should().OnlyContain(l => l.Href == null && l.ResolveFromPath == null);
        AssertNoExerciseLinksTheTemplate(model, "TestCourse");

        // The reading pages are unaffected — an anonymous visitor still browses the course itself.
        model.Home.Href.Should().Be($"TestCourse/{EducationLayoutAreas.LearnArea}");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_Area_RendersTheWholeCourseRail_OnAModulePage()
    {
        // End-to-end through the real area (the course query + the viewer's-copies query + the render),
        // deserialized exactly as the browser receives it: a NavMenu with the course home, the course-level
        // page, and ONE GROUP PER MODULE — proof the rail is no longer scoped to the current module.
        // The rail is LIVE, so we wait for the emission that has the full index rather than pinning the
        // first one (the query index is eventually consistent right after mesh start).
        var control = await RenderControl(
            EducationLayoutAreas.CourseNavArea, c => c is NavMenuControl { Areas.Count: 4 });

        var menu = control.Should().BeOfType<NavMenuControl>("the course rail is a NavMenu").Subject;
        menu.Areas.Count.Should().Be(4,
            "course home + the Introduction page + one group for each of the two modules");
    }

    // Every depth a learner can stand at, and the entry that must carry the "you are here" highlight there.
    // Depths 1-4 are pages the rail lists outright; 5 is a module's Exercise/ INDEX page (the live
    // .../Introduction/Exercises shape — the folder's own entry is replaced by the exercises it holds, so the
    // module home carries the highlight); 6-7 are the same exercise read from the template and from the
    // learner's own copy; 8 is a page BELOW everything the rail lists.
    public static TheoryData<string, string, string?> EveryDepth() => new()
    {
        { "TestCourse",                                            "TestCourse",                          null },
        { "TestCourse/Intro",                                      "TestCourse/Intro",                    null },
        { "TestCourse/Module2",                                    "TestCourse/Module2",                  "TestCourse/Module2" },
        { "TestCourse/Module2/Theory",                             "TestCourse/Module2/Theory",           "TestCourse/Module2" },
        { "TestCourse/Module2/Exercise",                           "TestCourse/Module2",                  "TestCourse/Module2" },
        { "TestCourse/Module2/Exercise/Drill1",                    "TestCourse/Module2/Exercise/Drill1",  "TestCourse/Module2" },
        { $"{LearnerId}/TestCourse/Module2/Exercise/Drill1",       "TestCourse/Module2/Exercise/Drill1",  "TestCourse/Module2" },
        { $"{LearnerId}/TestCourse/Module2/Exercise/Drill1/Step1", "TestCourse/Module2/Exercise/Drill1",  "TestCourse/Module2" },
    };

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(EveryDepth))]
    public async Task CourseNav_AtEveryDepth_TheIndexIsIdentical_AndOnlyTheMarkersMove(
        string currentPath, string expectedActive, string? expectedExpandedModule)
    {
        // THE requirement: "should only highlight where we stand, not reduce menu". Drilling down must not
        // re-scope or shrink the rail — at every depth the SAME index is emitted (same modules, same links,
        // same hrefs), and the only thing that changes is which entry is active and which module is open.
        var nodes = await QueryCourseSubtree();

        var atCourseRoot = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", "TestCourse", LearnerId, nodes, NoCopies);
        var here = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", currentPath, LearnerId, nodes, NoCopies);

        // 1. The index itself — module list, per-module links, hrefs, resolve targets — is byte-identical to
        //    the one rendered on the course home. It can never shrink as you go deeper.
        IndexSignature(here).Should().Be(IndexSignature(atCourseRoot),
            $"the course index must be the same at '{currentPath}' as at the course root — only the " +
            "highlight moves");
        here.Modules.Select(m => m.Path).Should().Equal("TestCourse/Module1", "TestCourse/Module2");

        // 2. Exactly ONE entry marks where we stand — the page itself, or the deepest entry containing it
        //    when the page is not a rail entry of its own.
        AllLinks(here).Where(l => l.IsActive).Select(l => l.SourcePath).Should().Equal(expectedActive);

        // 3. Collapsing hides links, never modules: every module is still listed, at most one is open.
        here.Modules.Where(m => m.Expanded).Select(m => m.Path)
            .Should().Equal(expectedExpandedModule is null ? [] : new[] { expectedExpandedModule });

        AssertNoExerciseLinksTheTemplate(here, "TestCourse");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_OnAModulesExerciseIndexPage_ShowsTheWholeIndex_AndItsOwnPosition()
    {
        // The live https://…/AgenticEngineering/Introduction/Exercises shape: the page IS a module's
        // exercise-folder index. Its own entry is replaced in the rail by the exercises it holds, so it gets
        // no exact link — and before the position fallback it left the learner with NOTHING highlighted.
        var nodes = await QueryCourseSubtree();

        var model = EducationLayoutAreas.BuildCourseNavModel(
            "TestCourse", "TestCourse/Module2/Exercise", LearnerId, nodes, NoCopies);

        // The whole course, exactly as on any other page.
        model.Modules.Select(m => m.Path).Should().Equal("TestCourse/Module1", "TestCourse/Module2");
        model.Pages.Select(p => p.SourcePath).Should().Equal("TestCourse/Intro");

        // Its own position: the module it belongs to is open and highlighted, with its exercises visible.
        var module2 = model.Modules.Single(m => m.Path == "TestCourse/Module2");
        module2.Expanded.Should().BeTrue("the learner is inside Module 2");
        // The nearest LISTED entry is the module itself — the folder's own entry is replaced by its exercises.
        AllLinks(model).Where(l => l.IsActive).Select(l => l.SourcePath).Should().Equal("TestCourse/Module2");
        module2.Links.Where(l => l.IsExercise).Select(l => l.SourcePath).Should().Equal(
            "TestCourse/Module2/Exercise/Drill1", "TestCourse/Module2/Exercise/Drill2");

        // And the exercises it lists still resolve into the learner's own copy, never the template.
        module2.Links.Where(l => l.IsExercise).Should().OnlyContain(l => l.Href == null);
        AssertNoExerciseLinksTheTemplate(model, "TestCourse");
    }

    [Fact(Timeout = 120_000)]
    public async Task CourseNav_Area_RendersTheWholeCourseRail_OnAnExerciseIndexPage()
    {
        // End-to-end proof of depth-invariance through the REAL area (course query + personal-copies query +
        // render): rendering the rail ON a module's exercise-index page yields the same NavMenu shape as on
        // a module page — course home + the course-level page + one group per module.
        var control = await RenderControl(
            EducationLayoutAreas.CourseNavArea,
            c => c is NavMenuControl { Areas.Count: 4 },
            node: "TestCourse/Module2/Exercise");

        var menu = control.Should().BeOfType<NavMenuControl>("the course rail is a NavMenu").Subject;
        menu.Areas.Count.Should().Be(4,
            "the index does not shrink on an exercise-index page — home + Introduction + both module groups");
    }

    [Fact]
    public void ResolveActivePath_PrefersTheExactEntry_ThenTheDeepestAncestor()
    {
        string[] entries =
        [
            "Course", "Course/M1", "Course/M1/Theory", "Course/M1/Exercise/Drill1", "Course/M2",
        ];

        // An entry that IS the page wins outright, however many ancestors also match.
        EducationLayoutAreas.ResolveActivePath("Course/M1/Theory", entries).Should().Be("Course/M1/Theory");
        EducationLayoutAreas.ResolveActivePath("Course", entries).Should().Be("Course");

        // No exact entry → the DEEPEST containing one, never a shallower ancestor.
        EducationLayoutAreas.ResolveActivePath("Course/M1/Exercise", entries).Should().Be("Course/M1");
        EducationLayoutAreas.ResolveActivePath("Course/M1/Exercise/Drill1/Step1", entries)
            .Should().Be("Course/M1/Exercise/Drill1");
        EducationLayoutAreas.ResolveActivePath("Course/Cover/Deep", entries).Should().Be("Course");

        // A sibling that merely shares a prefix is NOT an ancestor.
        EducationLayoutAreas.ResolveActivePath("Course/M11/Page", ["Course/M1"]).Should().BeNull();
        // Nothing contains the page → no position at all (the index still renders).
        EducationLayoutAreas.ResolveActivePath("Other/Page", entries).Should().BeNull();
        EducationLayoutAreas.ResolveActivePath("Course/M1", []).Should().BeNull();
    }

    [Fact]
    public void CourseNav_ACourseLevelExercise_IsAnExercise_NotATemplatePageLink()
    {
        // A childless exercise sitting directly under the COURSE (by node type, or a course-level Exercise/
        // folder that holds nothing) used to fall through to a plain page link — a live href straight at the
        // read-only template, the one thing the rail must never emit.
        var nodes = new[]
        {
            new MeshNode("Course") { Name = "Course" },
            new MeshNode("Cover", "Course") { Name = "Cover", Order = 0 },
            new MeshNode("Kata", "Course") { Name = "Kata", NodeType = "Edu/Exercise", Order = 1 },
            new MeshNode("Exercise", "Course") { Name = "Course Exercise", Order = 2 },
            new MeshNode("M1", "Course") { Name = "Module 1", Order = 3 },
            new MeshNode("Theory", "Course/M1") { Name = "Theory", Order = 1 },
        };

        var model = EducationLayoutAreas.BuildCourseNavModel("Course", "Course", "u1", nodes, NoCopies);

        model.Pages.Select(p => p.SourcePath).Should().Equal("Course/Cover", "Course/Kata", "Course/Exercise");
        model.Pages.Where(p => p.IsExercise).Select(p => p.SourcePath)
            .Should().Equal("Course/Kata", "Course/Exercise");
        model.Pages.Where(p => p.IsExercise).Should().OnlyContain(
            p => p.Href == null, "with no copy yet a course-level exercise resolves on click, never by href");
        // The plain cover page is untouched — only exercises are gated.
        model.Pages.Single(p => p.SourcePath == "Course/Cover").Href
            .Should().Be($"Course/Cover/{EducationLayoutAreas.LearnArea}");
        AssertNoExerciseLinksTheTemplate(model, "Course");
    }

    [Fact]
    public void PersonalExercisePath_ResolvesToTheViewersCopy_OrNothing()
    {
        // The "never link the source" invariant, pinned on the pure helper.
        EducationLayoutAreas.PersonalExercisePath("Course", "Course/M1/Exercise/Drill", "u1")
            .Should().Be("u1/Course/M1/Exercise/Drill");
        // Already the viewer's copy → unchanged (idempotent).
        EducationLayoutAreas.PersonalExercisePath("Course", "u1/Course/M1/Exercise/Drill", "u1")
            .Should().Be("u1/Course/M1/Exercise/Drill");
        // No writable home → no personal path exists; the caller must NOT fall back to the source.
        EducationLayoutAreas.PersonalExercisePath("Course", "Course/M1/Exercise/Drill", null)
            .Should().BeNull();
        EducationLayoutAreas.PersonalExercisePath("Course", "Course/M1/Exercise/Drill", "")
            .Should().BeNull();
        // Outside the course (or the course root itself) → null, never the source path.
        EducationLayoutAreas.PersonalExercisePath("Course", "Other/M1/Drill", "u1").Should().BeNull();
        EducationLayoutAreas.PersonalExercisePath("Course", "Course", "u1").Should().BeNull();
    }

    [Fact]
    public void ResolveCourseRoot_StripsTheViewerPartition()
    {
        // The rail is rooted at the COURSE — the first segment of the CENTRAL path, so a learner working in
        // their own copy still gets the shared course index.
        EducationLayoutAreas.ResolveCourseRoot("Course/M1/Page", null).Should().Be("Course");
        EducationLayoutAreas.ResolveCourseRoot("u1/Course/M1/Page", "u1").Should().Be("Course");
        // A different partition that merely LOOKS like the viewer's is not stripped.
        EducationLayoutAreas.ResolveCourseRoot("u12/Course/M1", "u1").Should().Be("u12");
        EducationLayoutAreas.ResolveCourseRoot("Course", "u1").Should().Be("Course");
        EducationLayoutAreas.ToSourcePath("u1/Course/M1", "u1").Should().Be("Course/M1");
        EducationLayoutAreas.ToSourcePath("Course/M1", "u1").Should().Be("Course/M1");
    }

    [Fact]
    public void IsExercise_MatchesNodeType_AndTheExerciseSubNamespace()
    {
        // Two signals: an exercise NODE TYPE (the Edu/Exercise plugin type, or core's "Exercise"), and the
        // {course}/{module}/Exercise/{n} folder convention.
        EducationLayoutAreas.IsExercise(
            new MeshNode("Kata", "Course/M1") { NodeType = "Edu/Exercise" }).Should().BeTrue();
        EducationLayoutAreas.IsExercise(
            new MeshNode("Kata", "Course/M1") { NodeType = "Exercise" }).Should().BeTrue();
        EducationLayoutAreas.IsExercise(
            new MeshNode("Drill", "Course/M1/Exercise") { NodeType = "Markdown" }).Should().BeTrue();
        EducationLayoutAreas.IsExercise(
            new MeshNode("Drill", "Course/M1/Exercises") { NodeType = "Markdown" }).Should().BeTrue();
        // A reading page — and the exercise FOLDER itself — are not exercises.
        EducationLayoutAreas.IsExercise(
            new MeshNode("Theory", "Course/M1") { NodeType = "Markdown" }).Should().BeFalse();
        EducationLayoutAreas.IsExercise(
            new MeshNode("Exercise", "Course/M1") { NodeType = "Markdown" }).Should().BeFalse();
    }

    [Fact]
    public void CourseNav_ExercisesByNodeType_AreListedLast_AndTargetThePersonalCopy()
    {
        // An exercise identified by NODE TYPE (no Exercise/ folder) is treated the same: listed after the
        // reading pages, resolving into the learner's own copy.
        var nodes = new[]
        {
            new MeshNode("Course") { Name = "Course" },
            new MeshNode("M1", "Course") { Name = "Module 1", Order = 1 },
            new MeshNode("Kata", "Course/M1") { Name = "Kata", NodeType = "Edu/Exercise", Order = 1 },
            new MeshNode("Theory", "Course/M1") { Name = "Theory", NodeType = "Markdown", Order = 2 },
        };

        var model = EducationLayoutAreas.BuildCourseNavModel(
            "Course", "Course/M1", "u1", nodes,
            new HashSet<string>(StringComparer.Ordinal) { "u1/Course/M1/Kata" });

        var module = model.Modules.Single();
        module.Links.Select(l => l.SourcePath).Should().Equal(
            "Course/M1", "Course/M1/Theory", "Course/M1/Kata");
        module.Links.Last().Href.Should().Be($"u1/Course/M1/Kata/{EducationLayoutAreas.LearnArea}");
        AssertNoExerciseLinksTheTemplate(model, "Course");
    }

    [Fact]
    public void CourseNav_ALoneExerciseLeaf_IsTheExercise_NotAFolder()
    {
        // A module whose "Exercise" child has no children of its own is not a FOLDER of exercises — it IS the
        // module's exercise, and must still resolve into the learner's copy rather than vanish from the rail.
        var nodes = new[]
        {
            new MeshNode("Course") { Name = "Course" },
            new MeshNode("M1", "Course") { Name = "Module 1", Order = 1 },
            new MeshNode("Theory", "Course/M1") { Name = "Theory", Order = 1 },
            new MeshNode("Exercise", "Course/M1") { Name = "Your Turn", Order = 2 },
        };

        var model = EducationLayoutAreas.BuildCourseNavModel("Course", "Course/M1", "u1", nodes, NoCopies);

        var module = model.Modules.Single();
        module.Links.Select(l => l.SourcePath).Should().Equal(
            "Course/M1", "Course/M1/Theory", "Course/M1/Exercise");
        var exercise = module.Links.Single(l => l.IsExercise);
        exercise.Href.Should().BeNull();
        exercise.ResolveFromPath.Should().Be("Course/M1/Exercise");
        AssertNoExerciseLinksTheTemplate(model, "Course");
    }

    [Fact]
    public void RenderCourseNav_EmitsHomeLink_CoursePages_AndOneGroupPerModule()
    {
        // The renderer is a straight projection of the model: home + course-level pages + one collapsible
        // group per module.
        var nodes = new[]
        {
            new MeshNode("Course") { Name = "Course" },
            new MeshNode("Cover", "Course") { Name = "Cover", Order = 0 },
            new MeshNode("M1", "Course") { Name = "Module 1", Order = 1 },
            new MeshNode("Page", "Course/M1") { Name = "Page", Order = 1 },
            new MeshNode("M2", "Course") { Name = "Module 2", Order = 2 },
            new MeshNode("Page", "Course/M2") { Name = "Page", Order = 1 },
        };
        var model = EducationLayoutAreas.BuildCourseNavModel(
            "Course", "Course/M1/Page", "u1", nodes, NoCopies);

        var menu = EducationLayoutAreas.RenderCourseNav(model);

        menu.Should().BeOfType<NavMenuControl>();
        menu.Areas.Count.Should().Be(1 + model.Pages.Count + model.Modules.Count,
            "one area for the course home, one per course-level page, one per module group");
        model.Modules.Should().HaveCount(2);
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

    // No personal copies of the course exist yet — the state a learner starts in.
    private static readonly IReadOnlySet<string> NoCopies = new HashSet<string>(StringComparer.Ordinal);

    // The live COURSE-scoped query the rail is built from (the same one CourseNavStream issues), so the
    // model tests are fed the REAL query result rather than a hand-made node list.
    private Task<IReadOnlyCollection<MeshNode>> QueryCourseSubtree()
        => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery("path:TestCourse scope:subtree is:main"))
            .Where(c => c.ChangeType == QueryChangeType.Initial).Take(1)
            .Select(c => (IReadOnlyCollection<MeshNode>)c.Items)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

    private static IEnumerable<EducationLayoutAreas.CourseNavLink> AllLinks(
        EducationLayoutAreas.CourseNavModel model)
        => EducationLayoutAreas.EnumerateLinks(model);

    // The rail's CONTENT, with the position markers (IsActive / Expanded) deliberately left OUT: everything
    // that must NOT change as the learner drills down. Two models with the same signature render the same
    // index.
    private static string IndexSignature(EducationLayoutAreas.CourseNavModel model)
        => string.Join("\n", new[] { $"HOME {model.Home.SourcePath} -> {model.Home.Href}" }
            .Concat(model.Pages.Select(Describe))
            .Concat(model.Modules.SelectMany(m =>
                new[] { $"MODULE {m.Path} '{m.Title}'" }.Concat(m.Links.Select(l => "  " + Describe(l))))));

    private static string Describe(EducationLayoutAreas.CourseNavLink link)
        => $"{(link.IsExercise ? "EXERCISE" : "PAGE")} {link.SourcePath} '{link.Title}' " +
           $"-> {link.Href ?? "(none)"} resolve={link.ResolveFromPath ?? "(none)"}";

    // THE invariant the maintainer cares about: an exercise entry may point into the learner's own
    // partition or nowhere at all — never at the central course template.
    private static void AssertNoExerciseLinksTheTemplate(
        EducationLayoutAreas.CourseNavModel model, string coursePath)
    {
        foreach (var link in AllLinks(model).Where(l => l.IsExercise))
        {
            link.Href.Should().NotBe(coursePath);
            link.Href.Should().NotStartWith(coursePath + "/",
                $"the exercise link '{link.Title}' must resolve into the learner's own copy, never the template");
        }
    }

    // Renders one area on a course node (TestCourse/Module1/Ex1 by default) through the layout client
    // (GetControlStream), so the assertion inspects the real deserialized UiControl the browser would
    // receive. The node is a parameter so the rail can be rendered at different DEPTHS.
    private async Task<UiControl?> RenderControl(
        string area, Func<UiControl, bool>? until = null, string node = "TestCourse/Module1/Ex1")
    {
        var client = GetClient();
        var nodeAddress = new Address(node);
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var reference = new LayoutAreaReference(area);
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);
        return await stream.GetControlStream(reference.Area!)
            .Where(c => c is not null && (until is null || until(c)))
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
