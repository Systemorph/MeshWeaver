#pragma warning disable CS1591

using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The built-in Agent/Skill content lives in the repo section <c>content/ai</c> (edited in the mesh,
/// synced back to the repo) but is ALSO embedded — via <c>LinkBase="Data"</c> — as the offline fallback
/// the providers read when the on-disk section can't be found (MAUI, a deployed container with no repo).
///
/// <para>The invariant is <b>set equality with the authored files</b>, not a floor. A floor
/// (<c>Count &gt;= 15</c>) passes while the embedded catalog quietly diverges from the on-disk one — and
/// that divergence is invisible in dev, because dev reads the on-disk section and only the SHIPPED build
/// falls back to the resources. A skill added to <c>content/ai</c> but not embedded therefore works
/// perfectly for every developer and is missing in production.</para>
/// </summary>
public class AiContentSectionTest
{
    [Theory]
    [InlineData("Agent")]
    [InlineData("Skill")]
    public void EmbeddedFallback_ShipsExactlyTheAuthoredFiles(string section)
    {
        // Both sides derived (and both ordinal-sorted) — this grows with the section and never needs
        // a test edit.
        AiContentSection.EmbeddedIds(section).Should().Equal(
            AiContentSection.FileIds(section),
            $"the embedded fallback must serve the SAME content/ai/{section} catalog the on-disk section "
            + "does; any difference is invisible in dev (which reads the files) and only shows up as "
            + "missing content in a shipped offline build");
    }
}
