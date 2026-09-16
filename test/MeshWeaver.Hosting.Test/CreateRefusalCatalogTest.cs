using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>#4507's two silent failure modes, neither of which <c>LocalizationTest</c> can see.</b>
///
/// <para><b>1. The catalog and the composed sentence drift.</b> A keyed refusal carries BOTH its
/// English (the wire value, and the fallback) and a catalog key whose English entry is what an
/// English viewer actually reads. Nothing holds those two together: reword one and the other stays,
/// and the result is two different sentences for the same refusal depending on which resolves. That
/// risk is highest exactly where the English is DERIVED rather than typed — <c>StoreReachability</c>
/// composes one sentence for three call sites precisely "so a user, an agent and a test all read the
/// same words", and a catalog copy of it is a fourth place that can fall behind.</para>
///
/// <para><b>2. A "translation" that is the English text.</b> <c>LocalizationTest</c> asserts every
/// English key HAS a German entry; it cannot assert the entry was translated. A copy-paste leaves
/// every gate green and every German viewer reading English — which is the bug #4507 reports, one
/// key at a time.</para>
///
/// <para>Both are asserted here over the refusals that are pure functions, so the guard needs no
/// mesh. The end-to-end half — that the HANDLERS actually reach for the keyed overloads — is
/// <c>CreateRefusalsAreLocalizedTest</c>.</para>
/// </summary>
public class CreateRefusalCatalogTest
{
    /// <summary>The refusal namespaces #4507 introduced, as the catalog prefixes they live under.</summary>
    private static readonly string[] Prefixes =
        ["activity.node.create.", "activity.node.bulkCreate.", "activity.satellite."];

    /// <summary>
    /// Every refusal this change can COMPOSE, paired with the text it must render in English. The
    /// list is the guard's denominator: a branch missing from it is a branch nothing checks, so the
    /// count is asserted below rather than left implicit.
    /// </summary>
    public static IEnumerable<object[]> ComposableRefusals()
    {
        foreach (var text in Composable())
            yield return [text.Key ?? "(verbatim)", text];
    }

    private static IEnumerable<LocalizableText> Composable()
    {
        // CreateNodesRequest.BulkRefusalText — all four structural branches.
        yield return Bulk(new MeshNode("", "P4507"));
        yield return Bulk(new MeshNode("bare", "P4507"));
        yield return Bulk(new MeshNode("sat", "P4507/_Thread") { NodeType = "Markdown" });
        yield return Bulk(new MeshNode("grant", "P4507") { NodeType = "AccessAssignment" });

        // ActivityNodeGuard.OwnerlessRefusal — both branches.
        yield return Ownerless(new MeshNode("t", "_Thread") { NodeType = "Markdown" });
        yield return Ownerless(new MeshNode("a", "P4507/_Activity")
        {
            NodeType = "Activity",
            MainNode = "",
        });

        // StoreReachability — the three availability verdicts, whose English is DERIVED from the
        // Describe* pair rather than typed, and therefore the likeliest to drift from the catalog.
        yield return StoreReachability.NodeCreationNotAttempted("P4507/some-node");
        yield return StoreReachability.BulkCreationNotAttempted(7);
        yield return StoreReachability.BulkCreationMayHavePartiallyLanded(7);
    }

    private static LocalizableText Bulk(MeshNode node)
    {
        var refusal = CreateNodesRequest.BulkRefusalText(node);
        Assert.NotNull(refusal);
        return refusal!.Value.Error;
    }

    private static LocalizableText Ownerless(MeshNode node)
    {
        var refusal = ActivityNodeGuard.OwnerlessRefusal(node);
        Assert.NotNull(refusal);
        return refusal!;
    }

    /// <summary>
    /// 🚨 The catalog's ENGLISH must reproduce the composed sentence exactly. When it does not, an
    /// English viewer and an English log read two different reports of one refusal — and because the
    /// fallback still works, nothing anywhere goes red.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComposableRefusals))]
    public void TheCatalogsEnglishReproducesTheComposedSentence(string key, LocalizableText refusal)
    {
        refusal.Key.Should().NotBeNullOrEmpty(
            "every refusal in this list is one this process AUTHORED, so it must carry a key — "
            + "Verbatim is for text no catalog can carry, and none of these is that");
        LocalizationCatalog.Keys.Should().Contain(key,
            "a key that is in no catalog renders as a raw token and falls back silently to the "
            + "English the writer happened to pass");
        refusal.Localize("en").Should().Be(refusal.English,
            "the catalog entry and the composed sentence are two copies of one refusal; the moment "
            + "they differ, which one a reader sees depends on whether the key resolved");
    }

    /// <summary>
    /// The German entry must be a TRANSLATION. Identical text is what a copy-paste leaves, and it is
    /// invisible to every other gate: the key is present, so completeness passes, and the viewer
    /// reads English.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComposableRefusals))]
    public void TheGermanEntryIsATranslationAndNotACopy(string key, LocalizableText refusal)
    {
        _ = key;
        refusal.Localize("de").Should().NotBe(refusal.Localize("en"),
            "an identical German rendering means either a missing entry (resolved through the "
            + "fallback) or an untranslated copy — a German viewer cannot tell those apart from a "
            + "refusal that was simply never keyed, which is #4507");
    }

    /// <summary>
    /// The same assertion across the WHOLE namespace rather than the composable subset, so a key
    /// added later without a real translation is caught too. Value-comparing is the check the
    /// plugins-repo mirror guard does and <c>LocalizationTest</c> deliberately cannot.
    /// </summary>
    [Fact]
    public void EveryCreateRefusalKeyIsTranslated()
    {
        var keys = LocalizationCatalog.Keys
            .Where(k => Prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        keys.Length.Should().BeGreaterThanOrEqualTo(26,
            "#4507 converted 26 create / bulk-create / satellite refusal keys; a lower count means "
            + "the guard has gone blind and is reporting a clean tree having checked almost nothing");

        var untranslated = keys
            .Where(k => string.Equals(
                LocalizationCatalog.Get(k, "de"), LocalizationCatalog.Get(k, "en"),
                StringComparison.Ordinal))
            .ToArray();

        untranslated.Should().BeEmpty(
            "these keys have a German entry identical to the English one: {0}",
            string.Join(", ", untranslated));
    }

    /// <summary>
    /// The guard's own denominator. A branch that stops composing a refusal — or a
    /// <see cref="MeshNode"/> shape that stops tripping it — would quietly shrink the list above to
    /// nothing while every assertion still passed.
    /// </summary>
    [Fact]
    public void TheComposableListCoversEveryBranchItClaimsTo()
        => Composable().Select(t => t.Key).Distinct().Should().HaveCount(9,
            "four BulkRefusalText branches, two OwnerlessRefusal branches and three "
            + "StoreReachability verdicts — nine distinct keys, each composed by a different branch");

    /// <summary>
    /// 🚨 The response factories, which are where the two halves of the decision are actually
    /// wired: <c>Error</c> keeps the English wire value, and the transcript carries the key.
    /// </summary>
    [Fact]
    public void TheKeyedFactoryFillsBothHalvesAndTheStringOneFillsNeither()
    {
        var refusal = LocalizableText.Keyed("Validation failed", "activity.node.create.validationFailed");

        var keyed = CreateNodeResponse.Fail(refusal, NodeCreationRejectionReason.ValidationFailed);
        keyed.Error.Should().Be("Validation failed", "Error is the ENGLISH wire value");
        keyed.Log.Should().NotBeNull();
        keyed.Log!.Messages.Should().ContainSingle().Which.MessageKey
            .Should().Be("activity.node.create.validationFailed");
        keyed.Log.Status.Should().Be(ActivityStatus.Failed);

        var bulk = CreateNodesResponse.Fail(refusal, NodeCreationRejectionReason.ValidationFailed, "P/x");
        bulk.Error.Should().Be("Validation failed");
        bulk.Log!.Messages.Should().ContainSingle().Which.MessageKey
            .Should().Be("activity.node.create.validationFailed");
        bulk.FailedPath.Should().Be("P/x");

        // The string overload stays, and stays honest: no key, so no transcript to render.
        CreateNodeResponse.Fail("raw", NodeCreationRejectionReason.Unknown).Log.Should().BeNull(
            "attaching a transcript with no key would advertise a localized surface that renders "
            + "English for every viewer — the pretence the Verbatim/Keyed split exists to prevent");
    }

    /// <summary>
    /// The exception boundary, where a response-only fix loses everything but the message. Verbatim
    /// text stamps NOTHING, so a reader falling back to <see cref="Exception.Message"/> is never
    /// handed a "localizable" sentence that is nothing of the kind.
    /// </summary>
    [Fact]
    public void OnlyAKeyedRefusalCrossesTheExceptionBoundary()
    {
        var keyed = CreateNodeResponse
            .Fail(LocalizableText.Keyed("Node path and Id must not be empty",
                    CreateNodesRequest.EmptyPathOrIdKey),
                NodeCreationRejectionReason.ValidationFailed)
            .ToException("P/x");

        keyed.RefusalText().Should().NotBeNull();
        keyed.RefusalText()!.Localize("de").Should().NotBe(keyed.Message);
        keyed.RefusalText()!.Localize("en").Should().Be(keyed.Message);

        var verbatim = CreateNodeResponse
            .Fail(LocalizableText.Verbatim("Npgsql said no"), NodeCreationRejectionReason.Unknown)
            .ToException("P/x");
        verbatim.RefusalText().Should().BeNull(
            "upstream words carry no key, and offering them as a localizable refusal would tell "
            + "the dialog it can translate text nobody here wrote");
    }
}
