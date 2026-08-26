using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins BOTH shipped constructor signatures of <see cref="LanguageModelCatalogSource"/> as binary
/// contracts.
///
/// <para><b>Why a reflection test and not a compile-time one.</b> The provider modules that call
/// these constructors — <c>MeshWeaver.AI.Anthropic</c>, <c>.OpenAI</c>, <c>.AzureFoundry</c>,
/// <c>.ClaudeCode</c>, <c>.Copilot</c>, <c>.WebSearch</c> — live in a DIFFERENT REPOSITORY, are
/// compiled separately, and land at runtime under <c>/data/modules</c>. Per
/// <c>ModuleLandingService</c> they "bind by simple name, and their contract is API compatibility,
/// not build identity", so nothing in this solution compiles against the binaries that will
/// actually make these calls. No source-level gate anywhere can see the break, and the
/// eight-parameter overload correspondingly has no source-level caller — that is expected, not a
/// sign it is dead.</para>
///
/// <para><b>The break this exists to prevent, and why it is invisible.</b> A call site that omits
/// the optional arguments still emits a call to the FULL primary constructor as it stood at ITS
/// compile time. Adding a parameter — <i>even one with a default</i> — REPLACES that signature
/// rather than extending it, so every already-landed module raises
/// <c>MissingMethodException</c> the instant its type initializer runs, which aborts the host.
/// Adding <c>Description</c> as a ninth parameter did exactly that: it passed every source gate
/// green (it had a default, so nothing failed to compile) and then crash-looped
/// <c>systemorph</c> for 100 minutes on the next roll — 24 restarts — while the old pods kept
/// serving, so every health probe stayed green and helm waited on a pod that could never start.
/// </para>
///
/// <para><b>Both arities are pinned, because removing one is the same mistake reversed.</b>
/// Dropping <c>Description</c> back out would have repaired every module compiled before the
/// change and broken every module compiled after it. So the ninth parameter stayed and the
/// eight-parameter form was restored beside it. <c>scripts/check-record-signatures.py</c> refuses
/// either direction at the diff; these tests hold the same line from inside the assembly, where
/// they still apply to a consumer that never sees the diff.</para>
///
/// <para><b>So: if a test here goes red, do NOT update it to match the new signature.</b> That is
/// the one change guaranteed to be wrong — it converts a caught break into a shipped one. Add the
/// new state as an <c>init</c> property, or add an overload preserving the old form.</para>
/// </summary>
public class LanguageModelCatalogSourceBinaryContractTest
{
    /// <summary>
    /// The form every AI provider module compiled before 2026-08-25 binds to. This list is a
    /// shipped contract; it may not change.
    /// </summary>
    private static readonly Type[] EightParameterForm =
    [
        typeof(string),                  // SectionName
        typeof(string),                  // ProviderName
        typeof(int),                     // Order
        typeof(string),                  // DisplayLabel
        typeof(string),                  // DefaultEndpoint
        typeof(ImmutableArray<string>),  // DefaultModelIds
        typeof(bool),                    // RequiresApiKey
        typeof(ProviderKind),            // Kind
    ];

    /// <summary>The current primary constructor, adding <c>Description</c>.</summary>
    private static readonly Type[] NineParameterForm =
        [.. EightParameterForm, typeof(string)];

    private static ConstructorInfo? CtorFor(Type[] parameters) =>
        typeof(LanguageModelCatalogSource)
            .GetConstructor(BindingFlags.Public | BindingFlags.Instance, parameters);

    [Fact]
    public void The_eight_parameter_constructor_still_exists()
    {
        Assert.True(CtorFor(EightParameterForm) is not null,
            "every AI provider module landed before 2026-08-25 calls exactly this constructor and "
            + "binds to it by name at runtime. If it was removed, those modules abort the host "
            + "with MissingMethodException on their first type initializer — which is what "
            + "crash-looped systemorph. It has no source-level caller by design; do not delete it "
            + "as dead code");
    }

    [Fact]
    public void The_nine_parameter_constructor_still_exists()
    {
        Assert.True(CtorFor(NineParameterForm) is not null,
            "modules compiled after Description was added bind to this form. Removing it to "
            + "'undo' the break would simply reverse who crashes — both arities are shipped, so "
            + "both stay");
    }

    /// <summary>
    /// The negative control: the assertions above must be able to fail. A <c>GetConstructor</c>
    /// that answered yes to anything would keep this suite green through the exact regression it
    /// exists to catch — the same "a discovery that finds nothing must never read as a pass" rule
    /// the surface-manifest and route gates follow.
    /// </summary>
    [Fact]
    public void A_constructor_shape_that_was_never_shipped_is_absent()
    {
        var neverShipped = NineParameterForm.Append(typeof(DateTimeOffset)).ToArray();

        Assert.True(CtorFor(neverShipped) is null,
            "if this matched, the assertions that the shipped constructors exist would be vacuous "
            + "and could not detect a signature change");
    }

    /// <summary>
    /// The eight-parameter form must produce the same value as the nine-parameter one with
    /// <c>Description</c> omitted — a delegating overload that drifted would be worse than none,
    /// because the two populations would silently disagree.
    /// </summary>
    [Fact]
    public void The_eight_parameter_form_agrees_with_the_nine_parameter_one()
    {
        var models = ImmutableArray.Create("claude-opus-4-8");

        var viaEight = (LanguageModelCatalogSource)CtorFor(EightParameterForm)!.Invoke(
            ["Anthropic", "Anthropic", 1, "Anthropic",
             "https://api.anthropic.com/v1/messages", models, true, ProviderKind.Api]);

        var viaNine = new LanguageModelCatalogSource(
            "Anthropic", "Anthropic", 1, "Anthropic",
            "https://api.anthropic.com/v1/messages", models, true, ProviderKind.Api, null);

        Assert.Equal(viaNine, viaEight);
        Assert.Null(viaEight.Description);
    }

    /// <summary>
    /// <c>Description</c> stays reachable and settable both ways — the repair must not have cost
    /// the feature the ninth parameter was added for.
    /// </summary>
    [Fact]
    public void Description_is_settable_positionally_and_through_with()
    {
        var source = new LanguageModelCatalogSource("Anthropic", "Anthropic")
        {
            Description = "Claude models, direct or Azure-hosted",
        };

        Assert.Equal("Claude models, direct or Azure-hosted", source.Description);
        Assert.Equal("changed", (source with { Description = "changed" }).Description);
    }
}
