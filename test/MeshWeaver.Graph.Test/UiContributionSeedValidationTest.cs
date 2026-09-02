using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The CONTROL ARM of <see cref="UiContributionSeedValidation"/>: every defect the validator claims
/// to catch is exercised here against a deliberately broken seed, so a validator that degenerated
/// into "return no problems" fails HERE rather than passing quietly wherever it is applied.
///
/// <para>That matters more than usual for this subject. Every defect below is invisible at runtime
/// — the contribution simply never appears, with no error, no warning and no placeholder. Six
/// shipped entries were dark for nine days that way (Systemorph/MeshWeaver.Plugins#1162) before
/// anyone noticed. A check that cannot fail would reproduce exactly that silence.</para>
/// </summary>
public class UiContributionSeedValidationTest
{
    private static MeshNode Seed(string id, UiContribution content, string? nodeType = null) =>
        new(id, "Admin/UiContribution")
        {
            Name = id,
            NodeType = nodeType ?? UiContributionNodeType.NodeType,
            Content = content,
        };

    private static UiContribution WellFormed => new()
    {
        Context = UiContribution.NodeSettingsContext,
        Area = "SettingsNotifications",
        Label = "Notifications",
        LabelKey = "settings.notifications",
    };

    [Fact]
    public void AWellFormedSeedSet_HasNoProblems()
    {
        Assert.Empty(UiContributionSeedValidation.Validate([Seed("Notifications", WellFormed)]));
    }

    [Fact]
    public void AContextNobodyDeclares_IsReported()
    {
        // THE defect this check exists for: a mistyped or retired context key renders NOWHERE.
        var problems = UiContributionSeedValidation.Validate(
            [Seed("Ghost", WellFormed with { Context = "NodeSettingz" })]);
        Assert.Contains(problems, p => p.Contains("NodeSettingz") && p.Contains("declared by nobody"));
    }

    [Fact]
    public void EveryPlatformContext_IsAccepted()
    {
        // The inventory must actually contain the keys the platform projects — a set that had
        // drifted empty would make the previous test pass for the wrong reason.
        Assert.All(UiContributionSeedValidation.PlatformContexts, context =>
            Assert.Empty(UiContributionSeedValidation.Validate(
                [Seed("C", WellFormed with { Context = context })])));
        Assert.Contains(UiContribution.SettingsContext, UiContributionSeedValidation.PlatformContexts);
        Assert.Contains(UiContribution.NodeSettingsContext, UiContributionSeedValidation.PlatformContexts);
    }

    [Fact]
    public void AContextIntroducedByATopBarDeclaration_IsAccepted_InTheSameSet()
    {
        // A TopBar declaration's Area IS a new context key; entries targeting it are legitimate.
        var declaration = Seed("ReinsuranceMenu", new UiContribution
        {
            Context = UiContribution.TopBarContext,
            Area = "Reinsurance",
            Label = "Reinsurance",
            LabelKey = "menu.reinsurance",
        });
        var entry = Seed("Treaties", new UiContribution
        {
            Context = "Reinsurance",
            Area = "TreatyList",
            Label = "Treaties",
            LabelKey = "menu.treaties",
        });

        Assert.Empty(UiContributionSeedValidation.Validate([declaration, entry]));
        // …and WITHOUT the declaration the very same entry is dark, which is the whole point.
        Assert.Contains(
            UiContributionSeedValidation.Validate([entry]),
            p => p.Contains("Reinsurance") && p.Contains("declared by nobody"));
    }

    [Fact]
    public void AnExtraContextTheCallerDeclares_IsAccepted()
    {
        var entry = Seed("Panel", WellFormed with { Context = "SidePanel" });
        Assert.Contains(UiContributionSeedValidation.Validate([entry]), p => p.Contains("declared by nobody"));
        Assert.Empty(UiContributionSeedValidation.Validate([entry], additionalContexts: ["SidePanel"]));
    }

    [Fact]
    public void AnEmptyArea_IsReported()
    {
        // Both projections drop an area-less entry BEFORE any gate runs, so it cannot even be
        // found by relaxing gates.
        Assert.Contains(
            UiContributionSeedValidation.Validate([Seed("NoArea", WellFormed with { Area = null })]),
            p => p.Contains("Area is empty"));
    }

    [Fact]
    public void AnAreaTheDeploymentDoesNotRegister_IsReported_WhenTheAreaListIsSupplied()
    {
        Assert.Empty(UiContributionSeedValidation.Validate(
            [Seed("Ok", WellFormed)], registeredAreas: ["SettingsNotifications"]));
        Assert.Contains(
            UiContributionSeedValidation.Validate(
                [Seed("Dangling", WellFormed with { Area = "SettingsTypo" })],
                registeredAreas: ["SettingsNotifications"]),
            p => p.Contains("SettingsTypo") && p.Contains("not a registered layout area"));
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/phish")]
    public void ANonPortalInternalHref_IsReported(string href)
    {
        // ResolveHref discards it and the entry quietly opens the DERIVED area URL — it renders,
        // it is clickable, and it goes somewhere else entirely.
        Assert.Contains(
            UiContributionSeedValidation.Validate([Seed("Link", WellFormed with { Href = href })]),
            p => p.Contains("not portal-internal"));
    }

    [Fact]
    public void APortalInternalHref_IncludingOneCarryingTheNodeToken_IsAccepted()
    {
        Assert.Empty(UiContributionSeedValidation.Validate(
            [Seed("Search", WellFormed with { Href = "/search?q=nodeType%3AThread" })]));
        Assert.Empty(UiContributionSeedValidation.Validate(
            [Seed("Approval", WellFormed with { Href = "/Approvals/Workspace/Request?doc={node}" })]));
    }

    [Fact]
    public void ANodeThatIsNotAUiContributionNode_IsReported()
    {
        // The catalog query is `nodeType:UiContribution` — a seed carrying any other node type is
        // never returned at all, however well-formed its content is.
        Assert.Contains(
            UiContributionSeedValidation.Validate([Seed("Wrong", WellFormed, nodeType: "Markdown")]),
            p => p.Contains("NodeType is 'Markdown'"));
    }

    [Fact]
    public void ContentThatIsNotAUiContribution_IsReported_RatherThanSkipped()
    {
        // The silent-null shape: content that is not the record reads as absent everywhere. Naming
        // the runtime type is the difference between a diagnosis and a mystery.
        var node = new MeshNode("Bad", "Admin/UiContribution")
        {
            NodeType = UiContributionNodeType.NodeType,
            Content = "not a contribution",
        };
        Assert.Contains(
            UiContributionSeedValidation.Validate([node]),
            p => p.Contains("not a readable UiContribution") && p.Contains("String"));
    }

    [Fact]
    public void TwoSeedsAtTheSamePath_AreReported()
    {
        // The catalog is a dictionary keyed on path: one silently replaces the other, and which
        // one survives depends on query order.
        Assert.Contains(
            UiContributionSeedValidation.Validate([Seed("Dup", WellFormed), Seed("Dup", WellFormed)]),
            p => p.Contains("two seeds share this path"));
    }

    [Fact]
    public void AnUntranslatedLabelOrGroup_IsReported()
    {
        // The portal ships English + German; a label with no key renders English for every viewer.
        Assert.Contains(
            UiContributionSeedValidation.Validate([Seed("Raw", WellFormed with { LabelKey = null })]),
            p => p.Contains("has no LabelKey"));
        Assert.Contains(
            UiContributionSeedValidation.Validate(
                [Seed("RawGroup", WellFormed with { Group = "Management" })]),
            p => p.Contains("has no GroupKey"));
        Assert.Empty(UiContributionSeedValidation.Validate(
            [Seed("Translated", WellFormed with
                { Group = "Management", GroupKey = "settings.groupManagement" })]));
    }
}
