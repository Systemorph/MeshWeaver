using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 #7355 — the compile watcher's refusals and the delivery-hold notices are persisted in
/// <see cref="NodeTypeDefinition.CompilationError"/> as English prose, and the NodeType page and
/// the instance error overlay render that field. A German viewer read English explanations and
/// recovery steps. Each refusal now also carries a keyed form
/// (<see cref="NodeTypeDefinition.CompilationErrorText"/>) that a reader renders in the VIEWER's
/// language — and only while it still says, in English, exactly what the stored text says.
/// </summary>
public class CompileRefusalTextTest
{
    private const string Type = "Acme/Widget";

    private static NodeTypeDefinition Held(string adopted, string current) => new()
    {
        CompilationStatus = CompilationStatus.Ok,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "Acme_Widget/v1.dll",
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        AdoptedSourceFingerprint = "572183a89fca620b",
        CurrentSourceFingerprint = "4ed23561cfd142d1",
        AdoptedModuleVersion = adopted,
        CurrentModuleVersion = current,
        BuildProvenance = BuildProvenance.AdoptedVerified,
    };

    /// <summary>
    /// Every keyed form renders, in English, EXACTLY the sentence its English producer writes — the
    /// two can never drift apart, which is what lets the English stay the wire value every reader,
    /// log and gate (<see cref="ClosedTypeSet.IsRefusal"/>) already uses.
    /// </summary>
    [Fact]
    public void EveryKeyedRefusal_RendersItsEnglishSentence_ExactlyInEnglish()
    {
        var def = Held("1.2.0", "2.0.0");
        var pairs = new (string Name, string English, IReadOnlyList<KeyedText> Parts)[]
        {
            ("requirePrebuilt", PrebuiltAssemblySeeder.RequiredParkReason(Type), PrebuiltAssemblySeeder.RequiredParkText(Type)),
            ("unknownMembership", PrebuiltAssemblySeeder.UnknownModuleMembershipParkReason(Type, "bundle X unreadable"),
                PrebuiltAssemblySeeder.UnknownModuleMembershipParkText(Type, "bundle X unreadable")),
            ("untracked, offered", PrebuiltAssemblySeeder.UntrackedPartitionParkReason(Type, true),
                PrebuiltAssemblySeeder.UntrackedPartitionParkText(Type, true)),
            ("declared module, no install record",
                PrebuiltAssemblySeeder.DeclaredModuleWithoutInstallRecordParkReason(Type, "1.1"),
                PrebuiltAssemblySeeder.DeclaredModuleWithoutInstallRecordParkText(Type, "1.1")),
            ("untracked, not offered", PrebuiltAssemblySeeder.UntrackedPartitionParkReason(Type, false),
                PrebuiltAssemblySeeder.UntrackedPartitionParkText(Type, false)),
            ("closed type set", ClosedTypeSet.RefusalFor(Type), ClosedTypeSet.RefusalText(Type)),
            ("closed type set, referenced", ClosedTypeSet.RefusalFor(Type, "Acme/w1"), ClosedTypeSet.RefusalText(Type, "Acme/w1")),
            ("incompatible", BuildDeliveryHold.IncompatibleNotice(def), BuildDeliveryHold.IncompatibleText(def)),
            ("too far behind", BuildDeliveryHold.TooFarBehindNotice(def, 3), BuildDeliveryHold.TooFarBehindText(def, 3)),
            ("incompatible + keyed gate",
                BuildDeliveryHold.IncompatibleNotice(def, PrebuiltAssemblySeeder.UntrackedPartitionParkReason(Type, false)),
                BuildDeliveryHold.WithGate(BuildDeliveryHold.IncompatibleText(def),
                    PrebuiltAssemblySeeder.UntrackedPartitionParkReason(Type, false),
                    PrebuiltAssemblySeeder.UntrackedPartitionParkText(Type, false))),
            ("incompatible + unkeyed gate",
                BuildDeliveryHold.IncompatibleNotice(def, "some upstream reason"),
                BuildDeliveryHold.WithGate(BuildDeliveryHold.IncompatibleText(def), "some upstream reason", null)),
        };

        foreach (var (name, english, parts) in pairs)
            KeyedTexts.Render(parts, "en").Should().Be(english, $"the keyed '{name}' refusal must say exactly its English sentence");
    }

    /// <summary>The point of the keys: a German viewer reads German, with the facts bound in.</summary>
    [Fact]
    public void AGermanViewer_ReadsTheRefusalInGerman()
    {
        var english = PrebuiltAssemblySeeder.UntrackedPartitionParkReason(Type, false);
        var parts = PrebuiltAssemblySeeder.UntrackedPartitionParkText(Type, false);

        var german = KeyedTexts.Localize(english, parts, "de");

        german.Should().NotBe(english);
        german.Should().StartWith("Nicht verfolgter Modulinhalt: Der NodeType 'Acme/Widget'")
            .And.Contain("Partition 'Acme'");
        KeyedTexts.Localize(english, parts, "en").Should().Be(english, "control: an English viewer reads the stored sentence");
    }

    /// <summary>
    /// The stamp: the gate settle's English and its keyed form travel together; a LATER writer that
    /// replaces the text (a Roslyn diagnostic, a park re-serve with a prepended finding) without the
    /// keys is rendered verbatim — the stale keys can never put words in its mouth.
    /// </summary>
    [Fact]
    public void TheStamp_KeysTheRefusal_AndAStaleKeyNeverOverridesNewText()
    {
        var reason = PrebuiltAssemblySeeder.RequiredParkReason(Type);
        var parts = PrebuiltAssemblySeeder.RequiredParkText(Type);
        var parked = new NodeTypeDefinition { CompilationStatus = CompilationStatus.Error, CompilationError = reason };

        var stamped = BuildDeliveryHold.StampErrorText(parked, reason, parts);
        stamped.CompilationErrorText.Should().NotBeNull();
        KeyedTexts.Localize(stamped.CompilationError, stamped.CompilationErrorText, "de")
            .Should().StartWith("Modules:RequirePrebuilt", "the config key itself is not translated")
            .And.Contain("Für den NodeType 'Acme/Widget'");

        // A later compile writes a Roslyn diagnostic and leaves the old keys on the record.
        var recompiled = stamped with { CompilationError = "Widget.cs(3,1): error CS1002: ; expected" };
        KeyedTexts.Localize(recompiled.CompilationError, recompiled.CompilationErrorText, "de")
            .Should().Be(recompiled.CompilationError, "keys that no longer reproduce the text are ignored");

        // …and the next stamp with no keyed reason clears them.
        BuildDeliveryHold.StampErrorText(recompiled, null, null).CompilationErrorText.Should().BeNull();
    }

    /// <summary>
    /// The too-far-behind notice carries its distance and its bound as FACTS, so a German viewer
    /// reads them in German. They were English phrases bound into the German sentence ("7 minor
    /// version(s) behind", "bound 5 (…)"), so the German notice still read half English. Every shape
    /// (measured or not, bound enabled or disabled) still reproduces its English exactly.
    /// </summary>
    [Fact]
    public void TheTooFarBehindNotice_ReadsGerman_ForEveryShapeOfDistanceAndBound()
    {
        var cases = new (string Name, NodeTypeDefinition Def, int Bound, string GermanDistance, string GermanBound)[]
        {
            ("measured, bounded", Held("1.2.0", "1.9.0"), 5, "7 Minor-Version(en) zurück", "Grenze 5 ("),
            ("unmeasured, bounded", Held("not-a-version", "1.9.0"), 5, "Abstand nicht messbar", "Grenze 5 ("),
            ("measured, disabled", Held("1.2.0", "1.9.0"), -1, "7 Minor-Version(en) zurück", "die Grenze ist DEAKTIVIERT"),
            ("unmeasured, disabled", Held("not-a-version", "1.9.0"), -1, "Abstand nicht messbar", "die Grenze ist DEAKTIVIERT"),
        };

        foreach (var (name, def, bound, germanDistance, germanBound) in cases)
        {
            var english = BuildDeliveryHold.TooFarBehindNotice(def, bound);
            var parts = BuildDeliveryHold.TooFarBehindText(def, bound);
            KeyedTexts.Render(parts, "en").Should().Be(english, $"'{name}' must reproduce its English exactly");

            var german = KeyedTexts.Localize(english, parts, "de")!;
            german.Should().StartWith("Build liegt zu weit hinter seiner Quelle zurück", name)
                .And.Contain(germanDistance, name)
                .And.Contain(germanBound, name)
                .And.Contain(StaleAdoptionBound.ConfigKey, $"'{name}': the config key itself is not translated");
            german.Should().NotContain("minor version(s) behind", name)
                .And.NotContain("distance not measurable", name)
                .And.NotContain("bound is DISABLED", name)
                .And.NotContain("bound 5", name);
        }
    }

    /// <summary>A record written before #7355 carries no keys and renders verbatim.</summary>
    [Fact]
    public void ALegacyRecordWithoutKeys_RendersItsStoredText()
    {
        var legacy = PrebuiltAssemblySeeder.RequiredParkReason(Type);
        KeyedTexts.Localize(legacy, null, "de").Should().Be(legacy);
    }

    /// <summary>A hold notice that lands in CompilationError is keyed by the stamp, gate included.</summary>
    [Fact]
    public void TheStamp_KeysAnIncompatibleHold_WithItsGate()
    {
        var reason = PrebuiltAssemblySeeder.UntrackedPartitionParkReason(Type, true);
        var parts = PrebuiltAssemblySeeder.UntrackedPartitionParkText(Type, true);
        var def = Held("1.2.0", "2.0.0") with
        {
            CompilationStatus = CompilationStatus.Unavailable,
            BuildProvenance = BuildProvenance.AdoptionRefused,
        };
        var settled = def with { CompilationError = BuildDeliveryHold.IncompatibleNotice(def, reason) };

        var stamped = BuildDeliveryHold.StampErrorText(settled, reason, parts);

        KeyedTexts.Reproduces(stamped.CompilationErrorText, stamped.CompilationError).Should().BeTrue();
        var german = KeyedTexts.Localize(stamped.CompilationError, stamped.CompilationErrorText, "de")!;
        german.Should().StartWith("Inkompatibler Build").And.Contain("Sperre: Nicht verfolgter Modulinhalt");
    }
}
