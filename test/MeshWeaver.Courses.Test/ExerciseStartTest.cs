using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// Exercises the fork: <see cref="ExerciseAttemptNodeType.StartAttempt"/>
/// creates the attempt node (InProgress, ExercisePath stamped) in the calling
/// user's partition plus the working-copy Code child seeded from the
/// exercise's starter (with <c>IsExecutable = true</c>).
/// </summary>
public class ExerciseStartTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task StartAttempt_CreatesAttemptWithStarterCopy()
    {
        var seeded = await SeedExercise(
            starterCode: "var x = 1;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");");

        var client = GetClient();
        var attemptPath = await ExerciseAttemptNodeType.StartAttempt(client, seeded.ExercisePath)
            .Should().Within(60.Seconds()).Emit();

        // The fork lands in the DevLogin admin's home partition, at the
        // canonical escaped path.
        var viewerHome = TestUsers.Admin.ObjectId;
        attemptPath.Should().Be(
            ExerciseAttemptNodeType.AttemptPathFor(viewerHome, seeded.ExercisePath));
        attemptPath.Should().StartWith($"{viewerHome}/Courses/");

        var workspace = client.GetWorkspace();
        var attempt = await workspace.GetMeshNodeStream(attemptPath)
            .Where(n => n?.Content is ExerciseAttemptStatus)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        attempt!.NodeType.Should().Be(ExerciseAttemptNodeType.NodeType);
        var status = (ExerciseAttemptStatus)attempt.Content!;
        status.Status.Should().Be(AttemptStatus.InProgress);
        status.ExercisePath.Should().Be(seeded.ExercisePath);
        status.RevealedSolution.Should().BeFalse();
        status.ValidationRequestedAt.Should().BeNull();

        // Working copy: starter code copied, executable.
        var codePath = $"{attemptPath}/{ExerciseNodeType.SourceSubNamespace}/{ExerciseAttemptNodeType.AttemptCodeNodeId}";
        var code = await workspace.GetMeshNodeStream(codePath)
            .Where(n => n?.Content is CodeConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        var codeContent = (CodeConfiguration)code!.Content!;
        codeContent.Code.Should().Be("var x = 1;");
        codeContent.IsExecutable.Should().BeTrue();
    }
}
