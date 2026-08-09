using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Pins the frontmatter contract of the shipped release notes (<c>Doc/WhatsNew</c>), which the
/// What's New settings tab reads to group and bundle them.
///
/// <para>The tab groups by ship day and renders a <c>Category: Feature</c> entry with its
/// <c>Description</c> while bundling the day's <c>Category: Fix</c> entries into one line, and the
/// doc tree sorts on <c>Order</c>. An entry missing any of those does not fail loudly — it renders
/// as a bare title in the wrong place, which is exactly how two entries authored with a lowercase
/// <c>title:</c>/<c>date:</c> shape sat unnoticed in the feed until 2026-08-09. This test is the
/// loud failure: it is cheaper to fail a PR than to notice a malformed note in production.</para>
/// </summary>
public class WhatsNewEntryIntegrityTest
{
    private const string WhatsNewResourcePrefix = "MeshWeaver.Documentation.Data.WhatsNew.";

    private static readonly string[] Categories = ["Feature", "Fix"];

    private static readonly Regex DatedName = new(@"^(?<date>\d{4}-\d{2}-\d{2})-.+$", RegexOptions.Compiled);

    [Fact]
    public void EveryEntry_CarriesTheFrontmatterTheFeedRendersFrom()
    {
        var entries = LoadEntries();
        entries.Should().NotBeEmpty("the What's New feed ships as embedded Doc/WhatsNew resources");

        var failures = new List<string>();

        foreach (var (id, content) in entries)
        {
            var match = DatedName.Match(id);
            if (!match.Success)
            {
                failures.Add($"{id}: file name must start with an ISO ship date (yyyy-MM-dd-<slug>.md) — " +
                             "the feed groups by that date");
                continue;
            }

            var frontMatter = ReadFrontMatter(content);
            if (frontMatter is null)
            {
                failures.Add($"{id}: no '---' frontmatter block");
                continue;
            }

            if (string.IsNullOrWhiteSpace(Field(frontMatter, "Name")))
                failures.Add($"{id}: missing 'Name:' — the feed links the entry by its name");

            var category = Field(frontMatter, "Category");
            if (!Categories.Contains(category, StringComparer.Ordinal))
                failures.Add($"{id}: Category is '{category ?? "(none)"}', must be one of " +
                             $"{string.Join(" | ", Categories)} — the feed renders Features in full " +
                             "and bundles Fixes into the day's summary");

            if (string.IsNullOrWhiteSpace(Field(frontMatter, "Description")))
                failures.Add($"{id}: missing 'Description:' — the one-line summary shown in the feed");

            var expectedOrder = $"-{match.Groups["date"].Value.Replace("-", "", StringComparison.Ordinal)}";
            var order = Field(frontMatter, "Order");
            if (order != expectedOrder)
                failures.Add($"{id}: Order is '{order ?? "(none)"}', must be '{expectedOrder}' " +
                             "(negative ship date) — otherwise the doc tree sorts it alphabetically by title");
        }

        failures.Should().BeEmpty(
            "every What's New entry must carry the frontmatter the feed renders from — see the " +
            "/pullrequest skill, step 0.5. Failures:\n{0}", string.Join("\n", failures));
    }

    /// <summary>The embedded entries, keyed by file name (<c>2026-08-09-some-slug.md</c>).</summary>
    private static IReadOnlyList<(string Id, string Content)> LoadEntries()
    {
        var assembly = typeof(DocumentationExtensions).Assembly;
        var entries = new List<(string, string)>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(WhatsNewResourcePrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            entries.Add((name[WhatsNewResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return entries;
    }

    /// <summary>The lines of the leading <c>---</c> block, or null when the file has none.</summary>
    private static IReadOnlyList<string>? ReadFrontMatter(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return null;

        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        return end < 0 ? null : lines[1..end];
    }

    /// <summary>
    /// The value of a frontmatter key. Case-sensitive on purpose: the mesh's markdown parser maps
    /// <c>Name</c>/<c>Category</c>/<c>Order</c> by their declared casing, so a lowercase
    /// <c>category:</c> would be silently dropped rather than accepted here.
    /// </summary>
    private static string? Field(IEnumerable<string> frontMatter, string key)
    {
        var prefix = key + ":";
        var line = frontMatter.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line?[prefix.Length..].Trim().Trim('"').Trim();
    }
}
