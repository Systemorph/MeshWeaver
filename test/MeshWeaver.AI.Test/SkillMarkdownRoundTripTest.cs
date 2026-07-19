#pragma warning disable CS1591

using System.IO;
using System.Linq;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The sync-back writes a mesh-edited skill back to its <c>.md</c> via <see cref="SkillMarkdown.Serialize"/>,
/// and the mesh reads it via <see cref="SkillMarkdown.Parse"/>. If those two ever disagree, a sync-back
/// corrupts the skill. This pins <c>Serialize ∘ Parse == identity</c> for EVERY built-in skill file, so
/// a round-trip is always lossless.
/// </summary>
public class SkillMarkdownRoundTripTest
{
    [Fact]
    public void EveryBuiltInSkill_RoundTrips_SerializeThenParse_Lossless()
    {
        var root = AiContentLocator.SectionRoot();
        Assert.NotNull(root);
        var skillDir = Path.Combine(root!, "Skill");
        Assert.True(Directory.Exists(skillDir), $"skill section not found: {skillDir}");

        var files = Directory.EnumerateFiles(skillDir, "*.md").OrderBy(f => f).ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var original = SkillMarkdown.Parse(File.ReadAllText(file), id);
            Assert.NotNull(original);

            var reparsed = SkillMarkdown.Parse(SkillMarkdown.Serialize(original!), id);
            Assert.NotNull(reparsed);

            Assert.Equal(original!.Name, reparsed!.Name);
            Assert.Equal(original.Description, reparsed.Description);
            Assert.Equal(original.Category, reparsed.Category);
            Assert.Equal(original.Icon, reparsed.Icon);
            Assert.Equal(original.Order, reparsed.Order);

            var od = Assert.IsType<SkillDefinition>(original.Content);
            var rd = Assert.IsType<SkillDefinition>(reparsed.Content);
            Assert.Equal(od.Instructions, rd.Instructions);
            Assert.Equal(od.AutoMount, rd.AutoMount);
            Assert.Equal(od.LaunchesSubThread, rd.LaunchesSubThread);
            Assert.Equal(od.Harness, rd.Harness);
            Assert.Equal(od.Action?.Kind, rd.Action?.Kind);
            Assert.Equal(od.Action?.Query, rd.Action?.Query);
            Assert.Equal(od.Action?.Field, rd.Action?.Field);
            Assert.Equal(od.Action?.Title, rd.Action?.Title);
            Assert.Equal(od.Action?.ContentPath, rd.Action?.ContentPath);
            Assert.Equal(od.Action?.Provider, rd.Action?.Provider);
        }
    }

    /// <summary>
    /// A skill file with malformed YAML front-matter (here: an unquoted <c>:</c> inside a value)
    /// must be <b>skipped</b> — <see cref="SkillMarkdown.Parse"/> returns null and never throws.
    /// Built-in skills load during mesh startup, so an uncaught YAML exception here would crash the
    /// whole host (this is exactly what took down every full-mesh Orleans integration test once).
    /// </summary>
    [Fact]
    public void Parse_MalformedFrontMatter_ReturnsNull_AndDoesNotThrow()
    {
        const string malformed =
            "---\n" +
            "nodeType: Skill\n" +
            "name: /broken\n" +
            "description: has an unquoted colon: right here which is invalid YAML\n" +
            "---\n\n" +
            "Body.\n";

        var node = SkillMarkdown.Parse(malformed, "broken");   // must not throw

        Assert.Null(node);
    }
}
