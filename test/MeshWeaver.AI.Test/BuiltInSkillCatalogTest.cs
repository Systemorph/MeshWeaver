#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The guard on the built-in skill catalog — the skill-side twin of <see cref="BuiltInAgentContentTest"/>,
/// which the agent half of <c>content/ai</c> got after the 2026-08-07 malformed-front-matter incident and
/// the skill half never did.
///
/// <para>🚨 What these replace, and why. Two tests used to assert the shipped catalog as a hardcoded
/// 19-element string literal, duplicated in two files. That shape is wrong twice:</para>
/// <list type="number">
///   <item><b>Brittle</b> — the set is DERIVED FROM FILES (<c>content/ai/Skill/*.md</c>), so adding a
///     legitimate skill turned CI red for a purely cosmetic reason, in every place the list was copied.</item>
///   <item><b>Blind</b> — and this is the one that matters. <see cref="BuiltInSkillProvider"/> SKIPS any
///     file <see cref="SkillMarkdown.Parse"/> cannot parse (it must: a throw during mesh startup takes
///     the host down). A malformed skill file therefore vanished with the shipped set unchanged and the
///     hardcoded expectation still GREEN. The suite could not detect a broken skill at all — the exact
///     defect a user reports as <i>"my skill doesn't appear and nothing is red"</i>.</item>
/// </list>
///
/// <para>So: assert the CONTRACT and the INVARIANTS, never a snapshot of today's data. The expected set
/// is derived from the files at both ends (it grows for free), and the silent skip is closed at the
/// root — the provider now RECORDS every file it drops, and
/// <see cref="SkillCatalog_LoadsEveryShippedFile_WithNoSkippedFile"/> fails red naming it.</para>
/// </summary>
public class BuiltInSkillCatalogTest
{
    /// <summary>A skill id is the slash word the user types (<c>/layout-area</c>) — lowercase kebab.</summary>
    private static readonly Regex SlashWord = new("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);

    /// <summary>
    /// The invariant that replaces the hardcoded 19-element literal: every authored file loads, and
    /// nothing ships that has no file. Count and membership are derived from the files at BOTH ends, so
    /// adding a skill needs no test edit — while a skill that stops parsing fails here BY NAME.
    /// </summary>
    [Fact]
    public void EveryShippedSkillFile_LoadsAsASkillNode()
    {
        var skills = new BuiltInSkillProvider().GetStaticNodes()
            .Where(n => n.NodeType == SkillNodeType.NodeType)
            .ToList();

        AiContentSection.AssertLoadedSetMatchesFiles(SkillNodeType.RootNamespace, skills.Select(n => n.Id));
    }

    /// <summary>
    /// The blind spot, closed. A skipped file is no longer invisible: the provider records it, and this
    /// test is the thing that goes RED — naming the file and the reason — when one appears. Without this
    /// the catalog can silently shrink and every other assertion here still passes.
    /// </summary>
    [Fact]
    public void SkillCatalog_LoadsEveryShippedFile_WithNoSkippedFile()
    {
        var failures = BuiltInSkillProvider.Catalog.Failures;

        failures.Should().BeEmpty(
            "a skill file that cannot be parsed is SKIPPED so mesh startup survives it — which means "
            + "this list is the ONLY place a dropped skill is visible. Fix the named file(s): "
            + string.Join(" | ", failures.Select(f => f.ToString())));
    }

    /// <summary>
    /// The other half of the same defect: a malformed file must be REPORTED, not merely dropped.
    /// Asserting only "returns null" cannot tell a loud skip from a silent one — and it was the silent
    /// one that let a broken skill disappear with a green suite. Assert the diagnostic exists and is
    /// actionable.
    /// </summary>
    [Fact]
    public void MalformedSkillFile_IsReported_NotSilentlyDropped()
    {
        // An unquoted ':' inside a value — the exact mistake that took every full-mesh suite red.
        const string malformed = """
            ---
            nodeType: Skill
            name: /broken
            description: has an unquoted colon: right here which is invalid YAML
            ---

            Body.
            """;

        // Must not throw: these load during mesh startup, so one bad file cannot stop the host.
        var node = SkillMarkdown.TryParse(malformed, "broken", out var error);

        node.Should().BeNull("a skill whose front matter does not parse cannot be built");
        error.Should().NotBeNullOrWhiteSpace(
            "the reason is what turns an invisible dropped skill into a diagnosable one");
        error.Should().Contain("YAML", "the author needs to know it is the front matter that is wrong");
    }

    /// <summary>
    /// And the loop is closed: the PROVIDER must record what the parser rejected.
    ///
    /// <para>🚨 Without this, <see cref="SkillCatalog_LoadsEveryShippedFile_WithNoSkippedFile"/> carries
    /// the same blind spot one level up — if the recording regressed (back to the original
    /// <c>if (node != null) nodes.Add(node);</c>), the failure list would be permanently empty and that
    /// test would pass forever while dropping skills silently. A test whose subject can only ever
    /// produce the passing value is not a test. So: feed a known-bad file through the real load path and
    /// assert a failure is recorded, NAMING the file.</para>
    /// </summary>
    [Fact]
    public void ProviderRecordsTheFileItSkipped_SoTheCatalogGuardCanEverFail()
    {
        var failures = new List<AiContentLoadFailure>();

        var node = BuiltInSkillProvider.ParseSkillNode(
            "no front matter here\n", "broken", "Skill/broken.md", failures);

        node.Should().BeNull();
        failures.Should().ContainSingle("the provider must record every file it drops");
        failures[0].File.Should().Be("Skill/broken.md",
            "naming the file is the whole diagnostic — without it the author cannot find the bad skill");
        failures[0].Reason.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>A file with no front matter at all is a different mistake and must say so — otherwise
    /// the author gets the misleading "invalid YAML" hint for a file that simply forgot the block.</summary>
    [Fact]
    public void SkillFileWithNoFrontMatter_IsReported_WithItsOwnReason()
    {
        var node = SkillMarkdown.TryParse("Just a body, no front matter.\n", "bare", out var error);

        node.Should().BeNull();
        error.Should().Contain("front matter");
    }

    /// <summary>
    /// The metadata contract every shipped skill must satisfy. A skill that parses is not yet a WORKING
    /// skill: a missing description is a blank row in the chat's skill list, a Pick action with no field
    /// opens a picker that fills nothing, and a skill with neither an action nor instructions does
    /// nothing at all when invoked. None of those are visible to a set-membership assertion.
    /// </summary>
    [Fact]
    public void EveryShippedSkill_CarriesTheMetadataTheChatNeeds()
    {
        var skills = new BuiltInSkillProvider().GetStaticNodes()
            .Where(n => n.NodeType == SkillNodeType.NodeType)
            .ToList();
        skills.Should().NotBeEmpty();

        skills.Select(n => n.Id).Should().OnlyHaveUniqueItems(
            "the id IS the slash word — two skills sharing one would shadow each other in the picker");

        foreach (var skill in skills)
        {
            skill.Namespace.Should().Be(SkillNodeType.RootNamespace, $"{skill.Id}: wrong partition");
            SlashWord.IsMatch(skill.Id).Should().BeTrue(
                $"'{skill.Id}' must be a typeable slash word (lowercase kebab, e.g. /layout-area)");
            skill.Name.Should().NotBeNullOrWhiteSpace($"{skill.Id}: no Name");
            skill.Description.Should().NotBeNullOrWhiteSpace(
                $"{skill.Id}: no Description — the chat's skill list and autocomplete show it, so a "
                + "skill without one is a blank row the user cannot tell apart");

            // A silently-degraded parse yields untyped content, which ProjectSkills drops — so the skill
            // is in the catalog but invisible in the chat. Assert the shape, not just "it loaded".
            skill.Content.Should().BeOfType<SkillDefinition>($"{skill.Id}: content is not a SkillDefinition");
            var def = (SkillDefinition)skill.Content!;

            (def.Action is not null || !string.IsNullOrWhiteSpace(def.Instructions)).Should().BeTrue(
                $"{skill.Id}: a skill must DO something — either an action the chat performs, or "
                + "instructions in the markdown body for the agent to load");

            if (def.Action?.Kind == SkillActionKind.Pick)
            {
                def.Action.Query.Should().NotBeNullOrWhiteSpace($"{skill.Id}: a Pick has nothing to pick from");
                def.Action.Field.Should().NotBeNullOrWhiteSpace(
                    $"{skill.Id}: a Pick with no composer field opens a picker whose selection goes nowhere");
            }
        }
    }
}
