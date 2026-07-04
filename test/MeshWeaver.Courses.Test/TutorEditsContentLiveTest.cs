using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// The tutor's live-edit chain: the module page embeds each theory block via
/// <see cref="LayoutAreaControl"/>, and an agent-shaped
/// <c>GetMeshNodeStream(theoryPath).Update(...)</c> under the signed-in user's
/// context (the same surface the Tutor agent's content edits ride) re-emits
/// the embedded theory area with the new content — no refresh, no re-query.
/// </summary>
public class TutorEditsContentLiveTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task TheoryUpdate_ReEmitsEmbeddedContent()
    {
        var seeded = await SeedExercise(
            starterCode: "var x = 1;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");");
        var theoryPath = await SeedTheory(seeded.ModulePath, "Intro", "Original theory text.");
        var client = GetClient();

        // The module page embeds the theory block (LayoutAreaControl → the
        // theory node's default area).
        var (moduleStream, moduleRef) = OpenArea(seeded.ModulePath, ModuleLayoutAreas.ContentArea);
        await moduleStream.GetControlStream(
                $"{moduleRef.Area}/{ModuleLayoutAreas.TheorySection}/Embed-Intro")
            .Should().Within(60.Seconds()).Match(c =>
                c is LayoutAreaControl embed && embed.Address.ToString()!.Contains(theoryPath));

        // The tutor's edit: the canonical stream.Update on the theory node,
        // under the calling user's context.
        const string newText = "Rewritten by the tutor: focus on the invariant.";
        await client.GetWorkspace().GetMeshNodeStream(theoryPath)
            .Update(curr => curr with { Content = new MarkdownContent { Content = newText } })
            .Should().Within(30.Seconds()).Emit();

        // Follow the embed: the theory node's own rendered area (what the
        // module page displays through the LayoutAreaControl) re-emits the new
        // content. The markdown body is a child area of the overview stack —
        // scan the first few auto-numbered slots reactively.
        var (theoryStream, theoryRef) = OpenArea(theoryPath, MarkdownLayoutAreas.OverviewArea);
        await Observable.Range(1, 8)
            .Select(i => theoryStream.GetControlStream($"{theoryRef.Area}/{i}"))
            .Merge()
            .Where(c => c is CollaborativeMarkdownControl markdown
                && (markdown.Value?.ToString() ?? "").Contains(newText))
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask();
    }
}
