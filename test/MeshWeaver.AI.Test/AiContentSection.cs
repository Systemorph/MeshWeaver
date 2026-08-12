#pragma warning disable CS1591

using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.AI;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The shared, reusable invariants for an <b>AI content section</b> — a <c>content/ai/&lt;Section&gt;</c>
/// folder of hand-authored <c>.md</c> files that a provider turns into the shipped catalog
/// (<c>Agent</c> → <see cref="BuiltInAgentProvider"/>, <c>Skill</c> → <see cref="BuiltInSkillProvider"/>).
///
/// <para>🚨 The principle these encode: <b>assert the CONTRACT and the INVARIANTS, not a snapshot of
/// today's data.</b> The shipped set is DERIVED FROM FILES, so a hardcoded expected list is wrong twice
/// over — it is <i>brittle</i> (adding a legitimate skill turns CI red for a cosmetic reason, in every
/// place the list was copied) and, far worse, it is <i>blind</i>: a provider skips a file it cannot
/// parse, so a malformed file leaves the shipped set unchanged and every hardcoded expectation stays
/// GREEN. The suite could not detect a broken skill — the exact defect a user reports as "my skill
/// doesn't appear and nothing is red".</para>
///
/// <para>The invariant that catches both: the loaded id set must EQUAL the file id set — derived from
/// the files at both ends, so it grows for free and fails the moment a file stops loading. These live
/// here rather than in one test class so the next <c>content/ai/&lt;Section&gt;</c> gets them free.</para>
/// </summary>
internal static class AiContentSection
{
    /// <summary>The on-disk directory for a section, asserted to exist (these tests are meaningless
    /// against the embedded fallback — they must read the authored files).</summary>
    public static string Directory(string section)
    {
        var root = AiContentLocator.SectionRoot();
        root.Should().NotBeNull("the AI content section (content/ai) must be resolvable from the test run");
        var dir = Path.Combine(root!, section);
        System.IO.Directory.Exists(dir).Should().BeTrue($"the {section} content section must exist at {dir}");
        return dir;
    }

    /// <summary>Every authored file's id in a section — the file name without its extension, which IS
    /// the node id (and, for a skill, the slash word). This is the derived expectation: it grows when a
    /// legitimate file is added and never needs editing.</summary>
    public static IReadOnlyList<string> FileIds(string section)
    {
        var ids = System.IO.Directory.EnumerateFiles(Directory(section), "*.md", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        ids.Should().NotBeEmpty($"the {section} section must ship at least one authored file");
        return ids;
    }

    /// <summary>The same ids as seen through the EMBEDDED fallback resources — what an offline build
    /// (MAUI / a deployed container without the repo section) actually serves.
    ///
    /// <para>Note for the first person to add a SUBDIRECTORY under a section: the two loaders disagree
    /// about ids there, and this will (correctly) fail. Resource names dot-separate path segments, so
    /// <c>Skill/sub/x.md</c> embeds as <c>…Data.Skill.sub.x.md</c> → the embedded loader's id is
    /// <c>sub.x</c>, while the on-disk loader takes the bare file name → <c>x</c>. Fix the loaders to
    /// agree; do not relax the assertion.</para></summary>
    public static IReadOnlyList<string> EmbeddedIds(string section) =>
        typeof(BuiltInAgentProvider).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith($"MeshWeaver.AI.Data.{section}.", StringComparison.Ordinal)
                        && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            // "MeshWeaver.AI.Data.Skill.agent.md" → "agent": drop the prefix, then the ".md".
            .Select(n => n[$"MeshWeaver.AI.Data.{section}.".Length..^".md".Length])
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The core invariant: every authored file in the section loaded, and nothing loaded that has no
    /// file. Both directions matter — <b>missing</b> is the silent-skip defect (a file was dropped and
    /// the catalog quietly shrank), <b>unexpected</b> means the catalog gained an entry no author can
    /// find or edit. The failure message NAMES the files, which is the diagnostic that was absent when
    /// this bit us.
    /// </summary>
    public static void AssertLoadedSetMatchesFiles(string section, IEnumerable<string> loadedIds)
    {
        var files = FileIds(section).ToHashSet(StringComparer.Ordinal);
        var loaded = loadedIds.ToHashSet(StringComparer.Ordinal);

        files.Except(loaded).OrderBy(x => x, StringComparer.Ordinal).Should().BeEmpty(
            $"every content/ai/{section}/*.md file must load into the shipped catalog — a file that "
            + "does not is SKIPPED silently, so the catalog shrinks with nothing red. The usual cause "
            + "is invalid YAML front matter (an unquoted ':' inside a value; quote the value)");

        loaded.Except(files).OrderBy(x => x, StringComparer.Ordinal).Should().BeEmpty(
            $"the shipped {section} catalog must not contain entries with no authored file — such an "
            + "entry cannot be edited or synced back");
    }
}
