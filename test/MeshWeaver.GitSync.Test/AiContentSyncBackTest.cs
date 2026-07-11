using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// End-to-end sync-back: the <see cref="AiContentDiskWriter"/> enumerates the live Skill partition
/// (served here by the in-memory <c>BuiltInSkillProvider</c>), reads each node authoritatively, and
/// writes it back to a <c>content/ai</c>-shaped folder as a <c>.md</c> file. Proves the query → stream
/// → serialize → disk plumbing; serialization correctness itself is pinned by the SkillMarkdown
/// round-trip test. Writes to a temp dir (never the repo working tree).
/// </summary>
public class AiContentSyncBackTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSkillType()   // serves the built-in skills (Skill/*), the enumeration target
            .ConfigureServices(s =>
            {
                s.AddGitHubSyncServices();   // registers AiContentDiskWriter
                return s;
            });

    [Fact]
    public async Task WriteBack_WritesTheLiveSkillPartition_ToDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-aicontent-syncback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await Mesh.SyncAiContentToRepo(dir).Timeout(TimeSpan.FromSeconds(60)).ToTask();

            // The built-in provider serves ~15 skills; every one should have been written.
            Assert.True(result.SkillsWritten >= 10, $"skills written: {result.SkillsWritten}");
            Assert.Equal(result.SkillsWritten,
                result.Files.Count(f => f.StartsWith("Skill/", StringComparison.Ordinal)));

            // Each written file re-parses to a valid Skill node (round-trip through the mesh + disk).
            var skillDir = Path.Combine(dir, "Skill");
            Assert.True(Directory.Exists(skillDir), $"expected {skillDir}");
            foreach (var file in Directory.EnumerateFiles(skillDir, "*.md"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var text = await File.ReadAllTextAsync(file);
                Assert.StartsWith("---", text);   // has YAML frontmatter
                var node = SkillMarkdown.Parse(text, id);
                Assert.NotNull(node);
                Assert.Equal(id, node!.Id);
                Assert.IsType<SkillDefinition>(node.Content);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
