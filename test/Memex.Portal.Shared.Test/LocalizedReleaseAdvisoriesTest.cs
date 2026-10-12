#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🌍 MeshWeaver#6540 — release-availability advisories are persisted with a reason CODE and its
/// arguments and rendered in the VIEWER's language, never as the English sentence for everyone.
///
/// <para>Pinned from both ends: every advisory the shared predicate composes carries a key whose
/// English template reproduces the stored English exactly (so the en rendering, the log line and
/// an older reader agree), and whose German template differs; the Updates tab renders a record's
/// items in the viewer's language; and a record that carries no code — written before #6540, or
/// carrying a code no catalog knows — renders its stored English unchanged.</para>
/// </summary>
public class LocalizedReleaseAdvisoriesTest
{
    private const string Tag = "9999.0.0-ci.1";
    private const string Identity = "s20261012localizedadvisories0000";

    /// <summary>A verdict carrying three advisory kinds at once: a boot compile (Feedback has no
    /// sealed bake), a declared floor above the target (Crm), and an install record naming a module
    /// the sealed set does not carry (Speech).</summary>
    private static UpdatabilityVerdict ThreeAdvisories() =>
        ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(Tag, Identity),
            [
                new RequiredPackage("Feedback", "Feedback"),
                new RequiredPackage("Crm", "Crm", MinMeshVersion: "99999.0.0"),
                new RequiredPackage("Speech", "Speech", HasContent: false) { ModuleName = "MeshWeaver.Speech" },
            ],
            ReleaseArtifacts.Of(["Crm"]) with
            {
                Modules = new SealedModuleSet(ImmutableDictionary<string, string>.Empty, [], null),
            });

    [Fact]
    public void EveryComposedAdvisory_IsKeyed_AndItsEnglishTemplateReproducesTheStoredSentence()
    {
        var verdict = ThreeAdvisories();
        var texts = verdict.AdvisoryTexts;

        texts.Select(t => t.Key).Should().Equal(
            [ReleaseAdvisoryCode.BootCompile, ReleaseAdvisoryCode.ModuleNotProduced, ReleaseAdvisoryCode.PlatformFloor],
            "each advisory kind carries its own reason code, in the order the verdict lists them");
        texts.Select(t => t.English).Should().Equal(verdict.Advisories,
            "the keyed items and the English lines are one list — they cannot disagree");

        foreach (var text in texts)
        {
            text.Localize("en").Should().Be(text.English,
                $"the en template of {text.Key} must render exactly the sentence the log and older readers see");
            text.Localize("de").Should().NotBe(text.English, $"{text.Key} has a German template");
        }
    }

    [Fact]
    public void TheWaivedPackageHold_LeadsKeyed_AndKeepsTheOtherAdvisoriesKeyed()
    {
        var packages = ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(Tag, Identity),
            [new RequiredPackage("Feedback", "Feedback"), new RequiredPackage("Crm", "Crm", MinMeshVersion: "99999.0.0")],
            ReleaseArtifacts.Of(["Crm"]),
            new ReleaseGatePolicy(RequirePrebuilt: true));

        var roll = packages.ForPlatformRoll();

        roll.AdvisoryTexts.Select(t => t.Key).Should().Equal(
            [ReleaseAdvisoryCode.PackagesDoNotFollow, ReleaseAdvisoryCode.PlatformFloor]);
        roll.AdvisoryTexts[0].Localize("en").Should().Be(roll.Advisories[0]);
        roll.AdvisoryTexts[0].Localize("de").Should().StartWith(
            "die Plattform wird unabhängig von den Paketen aktualisiert — 1 Paket(e)");
    }

    /// <summary>A producer that sets only the English (or an older writer) yields VERBATIM items —
    /// never a key bound to a sentence it does not describe.</summary>
    [Fact]
    public void AnEnglishOnlyAdvisory_IsVerbatim_NeverAKeyItDoesNotDescribe()
    {
        var keyed = ThreeAdvisories();
        var replaced = keyed with { Advisories = ["an advisory a host composed without a key"] };

        var item = replaced.AdvisoryTexts.Should().ContainSingle().Subject;
        item.Key.Should().BeNull();
        item.Localize("de").Should().Be("an advisory a host composed without a key");
    }

    [Fact]
    public void TheUpdatesTab_RendersTheItemsInTheViewersLanguage()
    {
        var verdict = ThreeAdvisories();
        var content = Recorded(verdict);

        var german = Render(content, "de");
        var english = Render(content, "en");

        german.Should().Contain("würde beim Start auf 9999.0.0-ci.1 neu kompiliert")
            .And.Contain("Feedback")
            .And.NotContain("would recompile at boot");
        english.Should().Contain("would recompile at boot on 9999.0.0-ci.1")
            .And.NotContain("würde beim Start");
    }

    /// <summary>A record written before #6540 carries only the joined English — it renders exactly
    /// that, for every viewer.</summary>
    [Fact]
    public void ALegacyRecord_RendersItsStoredSentence()
    {
        const string Stored = "would recompile at boot on 1.2.3 (no sealed content bake for framework identity sold): Feedback";
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            AdvisoriesTag = Tag,
            Advisories = Stored,
        };

        Render(content, "de").Should().Contain(Stored,
            "an unkeyed record has nothing to localize — its English is what it says");
    }

    /// <summary>Negative control on the fallback: a reason code the catalog does not carry (a
    /// module's own, or one that has since been renamed) renders the item's stored English, never a
    /// raw key and never another sentence.</summary>
    [Fact]
    public void AnUnknownCode_RendersTheStoredSentence()
    {
        const string Stored = "a module's own advisory, in English";
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            AdvisoriesTag = Tag,
            Advisories = Stored,
            AdvisoryItems = [new UpdateAdvisory(Stored) { Key = "releaseAdvisory.noSuchCode" }],
        };

        var german = Render(content, "de");

        german.Should().Contain(Stored).And.NotContain("releaseAdvisory.noSuchCode");
    }

    /// <summary>Every code of the platform's vocabulary is in BOTH catalogs, with the same named
    /// placeholders — a German template that dropped or renamed one would render a visible
    /// <c>{name}</c> gap.</summary>
    [Fact]
    public void EveryCode_IsInBothCatalogs_WithTheSamePlaceholders()
    {
        var codes = typeof(ReleaseAdvisoryCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        codes.Should().HaveCountGreaterThan(10, "the reflection read the vocabulary, not nothing");

        foreach (var code in codes)
        {
            LocalizationCatalog.KeysFor("en").Should().Contain(code);
            LocalizationCatalog.KeysFor("de").Should().Contain(code);
            Placeholders(LocalizationCatalog.Get(code, "de")).Order(StringComparer.Ordinal)
                .Should().Equal(Placeholders(LocalizationCatalog.Get(code, "en")).Order(StringComparer.Ordinal), code);
        }
    }

    private static UpdatePolicyContent Recorded(UpdatabilityVerdict verdict) => new()
    {
        LatestAvailableTag = Tag,
        AdvisoriesTag = Tag,
        Advisories = string.Join("; ", verdict.Advisories),
        AdvisoryItems = [.. verdict.AdvisoryTexts.Select(UpdateAdvisory.From)],
    };

    private static string Render(UpdatePolicyContent content, string locale) =>
        UpdatePolicySettingsTab.StatusMarkdown(
            content, (key, args) => LocalizationCatalog.Get(key, locale, args), locale: locale);

    private static ImmutableHashSet<string> Placeholders(string template) =>
        [.. Regex.Matches(template, @"\{([A-Za-z_][A-Za-z0-9_]*)\}").Select(m => m.Groups[1].Value)];
}
