#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 A HALF-LOCALIZED PACK MUST NOT MERGE — issue #1626.
///
/// <para>Skill and Agent nodes carry user-visible <c>name</c> / <c>description</c> rendered in the
/// skill picker, the agent combobox, autocomplete and the skill page. A missing translation is
/// INVISIBLE: the field falls back to English (deliberately — a hole would be worse), so a German
/// picker with three English rows in it looks like a design choice rather than a gap. Nothing is
/// red, nothing is logged, and the only person who finds out is the German user.</para>
///
/// <para><b>The rule, and what it deliberately does NOT demand.</b> The unit is the PACK, and the
/// requirement is derived from the pack itself: whatever languages the pack's nodes declare
/// BETWEEN them, every node must cover. An English-only pack requires nothing — the gate is "do
/// not ship half a language", never "you must ship every language", because forcing the second
/// would make adding a skill a translation project.</para>
///
/// <para>This is the node-DATA member of the localization family, alongside
/// <c>LocalizationTest</c> (the string catalog's key completeness) and the
/// <c>[Translation]</c> attribute tests. All three resolve a viewer's language through
/// <see cref="Locales"/>, which is what stops them disagreeing.</para>
/// </summary>
public class AiContentLocalizationTest
{
    private static IReadOnlyList<MeshNode> SkillPack => new BuiltInSkillProvider()
        .GetStaticNodes().Where(n => n.NodeType == SkillNodeType.NodeType).ToList();

    private static IReadOnlyList<MeshNode> AgentPack => new BuiltInAgentProvider()
        .GetStaticNodes().Where(n => n.NodeType == "Agent").ToList();

    /// <summary>The languages a pack declares between all its nodes — what every node must then cover.</summary>
    private static IReadOnlyCollection<string> PackLocales(IEnumerable<MeshNode> pack)
        => pack.SelectMany(n => NodeTextTranslations.DeclaredLocales(n.Content))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The guard, over a whole pack: no node may be missing a language its siblings ship.
    /// </summary>
    private static void AssertPackIsWhole(IReadOnlyList<MeshNode> pack, string packName)
    {
        pack.Should().NotBeEmpty($"the {packName} pack must actually load, or this guard checks nothing");
        var required = PackLocales(pack);

        var gaps = pack
            .Select(n => (n.Id, Missing: NodeTextTranslations.MissingTranslations(n, required)))
            .Where(x => x.Missing.Count > 0)
            .Select(x => $"{x.Id} → {string.Join(", ", x.Missing)}")
            .OrderBy(x => x, System.StringComparer.Ordinal)
            .ToArray();

        gaps.Should().BeEmpty(
            "the {0} pack declares [{1}], so EVERY node in it must carry those languages — a node "
            + "that does not falls back to English inside an otherwise translated list, which reads "
            + "as deliberate and is never reported. Missing: {2}",
            packName, string.Join(", ", required.OrderBy(l => l, System.StringComparer.Ordinal)),
            string.Join(" | ", gaps));
    }

    [Fact]
    public void BuiltInSkillPack_IsNotHalfLocalized() => AssertPackIsWhole(SkillPack, "built-in skill");

    [Fact]
    public void BuiltInAgentPack_IsNotHalfLocalized() => AssertPackIsWhole(AgentPack, "built-in agent");

    /// <summary>
    /// 🚨 THE CONTROL. The two assertions above pass trivially while the packs are English-only, so
    /// on their own they are not evidence the guard works. This drives the SAME rule over a
    /// deliberately half-localized pack and requires it to fail — and names exactly what is missing,
    /// because a guard that says only "something is wrong" gets ignored.
    /// </summary>
    [Fact]
    public void TheGuard_FailsOnAHalfLocalizedPack_AndNamesTheGap()
    {
        var translated = Skill("alpha", "Alpha", "Does alpha things", de: ("Alpha", "Macht Alpha-Dinge"));
        var untranslated = Skill("beta", "Beta", "Does beta things", de: null);
        var partial = Skill("gamma", "Gamma", "Does gamma things", de: ("Gamma", null));

        var pack = new[] { translated, untranslated, partial };
        var required = PackLocales(pack);
        // The requirement is DERIVED from the pack — one translated sibling is what makes de required.
        required.Should().Equal(["de"]);

        NodeTextTranslations.MissingTranslations(translated, required).Should().BeEmpty(
            "a fully translated node is complete");
        NodeTextTranslations.MissingTranslations(untranslated, required).Should().Equal(
            ["de:name", "de:description"]);
        // A node that translated only its NAME is exactly the half-localized case: the name renders
        // German and the help text beside it renders English, on the same row.
        NodeTextTranslations.MissingTranslations(partial, required).Should().Equal(["de:description"]);
    }

    /// <summary>
    /// The other direction: an English-only pack is COMPLETE, not broken. Without this the guard
    /// would be a demand to translate everything, which is how a localization gate gets disabled.
    /// </summary>
    [Fact]
    public void AnEnglishOnlyPack_RequiresNothing()
    {
        var pack = new[]
        {
            Skill("alpha", "Alpha", "Does alpha things", de: null),
            Skill("beta", "Beta", "Does beta things", de: null),
        };
        PackLocales(pack).Should().BeEmpty();
        foreach (var node in pack)
            NodeTextTranslations.MissingTranslations(node, PackLocales(pack)).Should().BeEmpty();
    }

    // ── Resolution ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The rendering rule: the viewer's language wins, per FIELD, and an untranslated field keeps
    /// its authored English rather than blanking.
    /// </summary>
    [Fact]
    public void Resolution_PrefersTheViewersLanguage_PerField()
    {
        var node = Skill("space", "/space", "Create a Space end-to-end", de: ("/space", null));

        node.LocalizedDescription("de").Should().Be("Create a Space end-to-end",
            "an untranslated field falls back to the authored text — never to a hole");
        node.LocalizedName("de").Should().Be("/space");

        var full = Skill("space", "Create a Space", "Create a Space end-to-end",
            de: ("Space anlegen", "Legt einen Space vollständig an"));
        full.LocalizedName("de").Should().Be("Space anlegen");
        full.LocalizedDescription("de").Should().Be("Legt einen Space vollständig an");

        full.LocalizedName("en").Should().Be("Create a Space", "English IS the authored text");
        full.LocalizedName(null).Should().Be("Create a Space", "an unknown viewer degrades to English");
        full.LocalizedName("fr").Should().Be("Create a Space",
            "a language this deployment does not ship resolves to English, never to a raw key");
    }

    /// <summary>
    /// Region variants fold onto the primary subtag exactly as everywhere else — the same
    /// <see cref="Locales.Resolve"/> the string catalog uses, which is what stops a viewer on
    /// <c>de-CH</c> seeing a German page frame around an English skill list.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("de-CH")]
    [InlineData("de-AT")]
    [InlineData("DE")]
    public void RegionVariants_FoldOntoThePrimarySubtag(string requested)
        => Skill("x", "X", "English", de: ("X", "Deutsch"))
            .LocalizedDescription(requested).Should().Be("Deutsch");

    /// <summary>
    /// 🚨 NON-WIDENING, the localization edition: translating a label must never change what an
    /// invocation resolves to. A skill is invoked by its node <b>Id</b> and an agent by path, so
    /// those are wire tokens; the projection carries the Id through untouched in every language.
    /// </summary>
    [Fact]
    public void TheInvocationToken_IsNeverLocalized()
    {
        var node = Skill("space", "Create a Space", "…", de: ("Space anlegen", "…"));
        var options = new System.Text.Json.JsonSerializerOptions();

        var english = SkillNodeType.ProjectSkills([node], options, "en").Single();
        var german = SkillNodeType.ProjectSkills([node], options, "de").Single();

        german.Id.Should().Be(english.Id).And.Be("space",
            "the slash word is the node Id — a German viewer still types /space, because the router "
            + "resolves the token, not the label");
        german.Name.Should().Be("Space anlegen");
        english.Name.Should().Be("Create a Space");
    }

    /// <summary>
    /// The model-facing half stays English even when the picker label is German — an agent's
    /// <c>AgentConfiguration.Description</c> is the delegation catalogue a MODEL reads to choose an
    /// agent, so translating it would change routing rather than presentation.
    /// </summary>
    [Fact]
    public void TheModelFacingDescription_StaysAuthored()
    {
        var config = new AgentConfiguration
        {
            Id = "Tutor",
            Description = "Guides trainees through a course.",
            Translations = new Dictionary<string, LocalizedNodeText>
            {
                ["de"] = new() { Name = "Tutor", Description = "Begleitet Lernende durch einen Kurs." },
            },
        };
        var node = new MeshNode("Tutor", "Agent")
        {
            NodeType = "Agent",
            Name = "Tutor",
            Description = "Guides trainees through a course.",
            Content = config,
        };

        var info = AgentPickerProjection.ToAgentDisplayInfo(node, new System.Text.Json.JsonSerializerOptions(), "de");
        info.Should().NotBeNull();
        info!.Description.Should().Be("Begleitet Lernende durch einen Kurs.",
            "the PICKER shows the viewer's language");
        info.AgentConfiguration!.Description.Should().Be("Guides trainees through a course.",
            "the delegation catalogue the MODEL reads must stay the authored text — translating it "
            + "would make agent selection depend on the viewer's UI language");
    }

    private static MeshNode Skill(
        string id, string name, string description, (string? Name, string? Description)? de)
        => new(id, SkillNodeType.RootNamespace)
        {
            NodeType = SkillNodeType.NodeType,
            Name = name,
            Description = description,
            Category = "Skills",
            Content = new SkillDefinition
            {
                Instructions = "body",
                Translations = de is null
                    ? null
                    : new Dictionary<string, LocalizedNodeText>
                    {
                        ["de"] = new() { Name = de.Value.Name, Description = de.Value.Description },
                    },
            },
        };
}
