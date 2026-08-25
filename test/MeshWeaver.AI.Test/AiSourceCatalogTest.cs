#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the ONE resolution seam (<see cref="AiSourceCatalog"/>): the named/described defaults
/// reproduce the platform's historical queries exactly, placeholders expand (legacy spellings
/// included), a template whose placeholder has no value is DROPPED — never emitted half-expanded —
/// an unanchored query is never emitted, user entries override defaults, legacy bare templates are
/// still honored, and the platform model path carries its disclosure and terms link.
/// </summary>
public class AiSourceCatalogTest
{
    private static readonly AiSourceContext Full = AiSourceCatalog.Context(
        userPath: "alice", contextPath: "ACME/Project/Todo", nodeTypePath: "Office/Slide");

    [Fact]
    public void Defaults_AreNamedAndDescribed_ForEveryKind()
    {
        foreach (var kind in AiSourceKinds.All)
        {
            var defaults = AiSourceCatalog.Defaults(kind);
            Assert.NotEmpty(defaults);
            Assert.All(defaults, d =>
            {
                Assert.Equal(kind, d.Kind);
                Assert.False(string.IsNullOrWhiteSpace(d.Name), $"{kind} default without a name: {d.Query}");
                Assert.False(string.IsNullOrWhiteSpace(d.Description), $"{kind} default without a description: {d.Name}");
                Assert.False(string.IsNullOrWhiteSpace(d.Query));
            });
        }
    }

    [Fact]
    public void Defaults_ReproduceTheHistoricalQueries_Exactly()
    {
        // Skills: the four rows AiSettingsNodeType has always resolved, in order.
        Assert.Equal(
            AiSettingsNodeType.DefaultSkillQueryTemplates,
            AiSourceCatalog.SkillDefaults.Select(d => d.Query).ToImmutableArray());
        // Agents: the one canonical registry template.
        Assert.Equal(
            AiSettingsNodeType.DefaultAgentQueryTemplates,
            AiSourceCatalog.AgentDefaults.Select(d => d.Query).ToImmutableArray());
        // Models: BuildModelQueries' rows for the tokenized context.
        Assert.Equal(
            AgentPickerProjection.BuildModelQueries(
                AiSourceCatalog.ObjectPathToken, AiSourceCatalog.NodeTypePathToken, null, AiSourceCatalog.UserToken),
            AiSourceCatalog.ModelDefaults.Select(d => d.Query).ToArray());
    }

    [Fact]
    public void ResolvedSkillDefaults_EqualTheCanonicalBuilder_InEveryContextShape()
    {
        // Full context, no user, no type, nothing — the seam must equal what SkillNodeType.SkillQueries
        // produced before it existed (this is the "nothing regresses" guarantee).
        foreach (var (user, ctx, type) in new (string?, string?, string?)[]
                 {
                     ("alice", "ACME/Project", "Office/Slide"),
                     (null, "ACME/Project", null),
                     ("alice", null, null),
                     ("alice", "login/whatever", "settings/x"), // reserved partitions drop
                 })
        {
            var expected = SkillNodeType.SkillQueries(ctx, user, type);
            var actual = AiSettingsNodeType.ResolveSkillQueries(null, ctx, type, user);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ResolvedModelDefaults_EqualTheCanonicalBuilder()
    {
        foreach (var (user, ctx, type) in new (string?, string?, string?)[]
                 {
                     ("alice", "ACME/Project", "Office/Slide"),
                     (null, null, null),
                     ("alice", "welcome/x", null),
                 })
        {
            var expected = AgentPickerProjection.BuildModelQueries(
                AgentPickerProjection.IsReservedPartition(ctx) ? null : ctx, type, null, user);
            var actual = AiSettingsNodeType.ResolveModelQueries(null, ctx, type, user);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Expand_SubstitutesNewAndLegacyPlaceholders()
    {
        Assert.Equal("namespace:alice/Skill nodeType:Skill",
            AiSourceCatalog.Expand(AiSourceKinds.Skill, "namespace:{user}/Skill nodeType:Skill", Full, out _));
        Assert.Equal("namespace:alice/Skill nodeType:Skill",
            AiSourceCatalog.Expand(AiSourceKinds.Skill, "namespace:{userPath}/Skill nodeType:Skill", Full, out _));
        Assert.Equal("path:ACME scope:descendants nodeType:Skill",
            AiSourceCatalog.Expand(AiSourceKinds.Skill, "path:{objectPartition} scope:descendants nodeType:Skill", Full, out _));
        Assert.Equal("path:ACME/Project/Todo nodeType:Skill",
            AiSourceCatalog.Expand(AiSourceKinds.Skill, "path:{objectPath} nodeType:Skill", Full, out _));
        Assert.Equal("path:Office scope:descendants nodeType:Skill",
            AiSourceCatalog.Expand(AiSourceKinds.Skill, "path:{nodeTypePartition} scope:descendants nodeType:Skill", Full, out _));
        // Legacy {currentPath}: the partition for skills/agents, the full path for models — exactly
        // what each builder substituted before the seam existed.
        Assert.Equal("path:ACME scope:descendants nodeType:Skill",
            AiSourceCatalog.Expand(AiSourceKinds.Skill, "path:{currentPath} scope:descendants nodeType:Skill", Full, out _));
        Assert.Equal("namespace:ACME/Project/Todo/Provider nodeType:LanguageModel",
            AiSourceCatalog.Expand(AiSourceKinds.Model, "namespace:{currentPath}/Provider nodeType:LanguageModel", Full, out _));
    }

    [Fact]
    public void Expand_DropsWhenAPlaceholderHasNoValue_NeverHalfExpanded()
    {
        var noType = AiSourceCatalog.Context("alice", "ACME/Project", null);
        var query = AiSourceCatalog.Expand(AiSourceKinds.Skill,
            "path:{nodeTypePartition} scope:descendants nodeType:Skill", noType, out var reason);
        Assert.Null(query);
        Assert.Contains("{nodeTypePartition}", reason);

        var signedOut = AiSourceCatalog.Context(null, "ACME", null);
        Assert.Null(AiSourceCatalog.Expand(AiSourceKinds.Skill, "namespace:{user}/Skill nodeType:Skill", signedOut, out _));
    }

    [Fact]
    public void Expand_RefusesUnanchoredAndUnknownPlaceholders()
    {
        Assert.Null(AiSourceCatalog.Expand(AiSourceKinds.Skill, "nodeType:Skill", Full, out var reason));
        Assert.Contains("anchored", reason);
        Assert.Null(AiSourceCatalog.Expand(AiSourceKinds.Skill, "namespace:{space}/Skill nodeType:Skill", Full, out reason));
        Assert.Contains("{space}", reason);
        Assert.Null(AiSourceCatalog.Expand(AiSourceKinds.Skill, "   ", Full, out reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Resolve_UserEntriesWinOverDefaults_LegacyTemplatesStillHonored()
    {
        var named = new AiSettings
        {
            SkillSources = ImmutableArray.Create(new AiSourceEntry
            {
                Kind = AiSourceKinds.Skill, Name = "Team skills",
                Description = "Our shared team skills.", Query = "namespace:Team/Skill nodeType:Skill",
            }),
            SkillQueries = ImmutableArray.Create("namespace:Legacy/Skill nodeType:Skill"),
        };
        var resolved = AiSourceCatalog.Resolve(AiSourceKinds.Skill, named, Full);
        Assert.Single(resolved);
        Assert.Equal("Team skills", resolved[0].Entry.Name);
        Assert.Equal("namespace:Team/Skill nodeType:Skill", resolved[0].Query);
        Assert.False(resolved[0].IsDefault);

        var legacy = new AiSettings
        {
            SkillQueries = ImmutableArray.Create(
                AiSettingsNodeType.DefaultSkillQueryTemplates[0],
                "namespace:Legacy/Skill nodeType:Skill"),
        };
        var annotated = AiSourceCatalog.Resolve(AiSourceKinds.Skill, legacy, Full);
        Assert.Equal(2, annotated.Length);
        Assert.Equal("Platform skills", annotated[0].Entry.Name);   // a default's template keeps its identity
        Assert.True(annotated[0].IsDefault);
        Assert.Equal("Custom source", annotated[1].Entry.Name);
        Assert.False(annotated[1].IsDefault);

        var none = AiSourceCatalog.Resolve(AiSourceKinds.Skill, new AiSettings(), Full);
        Assert.Equal(AiSourceCatalog.SkillDefaults.Length, none.Length);
        Assert.All(none, r => Assert.True(r.IsDefault));
    }

    [Fact]
    public void Queries_KeepOnlyActiveEntries_Deduplicated()
    {
        var settings = new AiSettings
        {
            SkillSources = ImmutableArray.Create(
                new AiSourceEntry { Kind = AiSourceKinds.Skill, Name = "A", Query = "namespace:X/Skill nodeType:Skill" },
                new AiSourceEntry { Kind = AiSourceKinds.Skill, Name = "B", Query = "namespace:x/skill nodeType:Skill" },
                new AiSourceEntry { Kind = AiSourceKinds.Skill, Name = "C", Query = "nodeType:Skill" },
                new AiSourceEntry { Kind = AiSourceKinds.Skill, Name = "D", Query = "namespace:{nodeTypePartition}/Skill nodeType:Skill" }),
        };
        var queries = AiSourceCatalog.Queries(AiSourceCatalog.Resolve(
            AiSourceKinds.Skill, settings, AiSourceCatalog.Context("alice", "ACME", null)));
        Assert.Equal(new[] { "namespace:X/Skill nodeType:Skill" }, queries);
    }

    [Fact]
    public void Validate_RejectsWhatTheSeamWouldDrop()
    {
        Assert.Null(AiSourceCatalog.Validate(new AiSourceEntry
        { Kind = "skill", Name = "ok", Query = "namespace:{user}/Skill nodeType:Skill" }));
        Assert.NotNull(AiSourceCatalog.Validate(new AiSourceEntry { Kind = "skill", Name = "", Query = "namespace:X nodeType:Skill" }));
        Assert.NotNull(AiSourceCatalog.Validate(new AiSourceEntry { Kind = "thing", Name = "x", Query = "namespace:X nodeType:Skill" }));
        Assert.Contains("anchored", AiSourceCatalog.Validate(new AiSourceEntry { Kind = "model", Name = "x", Query = "nodeType:LanguageModel" }));
        Assert.Contains("{typo}", AiSourceCatalog.Validate(new AiSourceEntry { Kind = "agent", Name = "x", Query = "namespace:{typo}/Agent nodeType:Agent" }));
    }

    [Fact]
    public void PlatformModelPath_IsLabeledMeshWeaverOpenRouter_WithTheDisclosureAndTermsLink()
    {
        var platform = AiSourceCatalog.ModelDefaults[0];
        Assert.Equal(AiSourceCatalog.MeshWeaverOpenRouterLabel, platform.Name);
        Assert.Equal(AiSourceCatalog.OpenRouterDisclosure, platform.Description);
        Assert.Contains(AiSourceCatalog.OpenRouterTermsUrl, platform.Description);
        Assert.Contains("OpenRouter", platform.Description);
        Assert.StartsWith($"namespace:{ModelProviderNodeType.RootNamespace} ", platform.Query);
        // The disclosure is seeded onto the provider node — one text, one place.
        Assert.Equal(AiSourceCatalog.OpenRouterDisclosure,
            BuiltInLanguageModelProvider.ProviderDescription("OpenRouter", null));
        Assert.Equal("custom", BuiltInLanguageModelProvider.ProviderDescription("OpenRouter", "custom"));
        Assert.Null(BuiltInLanguageModelProvider.ProviderDescription("Anthropic", null));
    }

    [Fact]
    public void MergePackageSources_AppendNamedRows_WhenTheUserMaintainsNamedSources()
    {
        var named = new AiSettings
        {
            SkillSources = AiSourceCatalog.SkillDefaults,
            AgentSources = AiSourceCatalog.AgentDefaults,
        };
        var merged = AiSettingsNodeType.MergePackageSources(named, "Office");
        Assert.Equal(AiSourceCatalog.SkillDefaults.Length + 1, merged.SkillSources.Length);
        Assert.Equal(AiSourceCatalog.AgentDefaults.Length + 1, merged.AgentSources.Length);
        Assert.Equal("Office package", merged.SkillSources[^1].Name);
        Assert.Equal("namespace:Office/Skill nodeType:Skill", merged.SkillSources[^1].Query);
        // Idempotent.
        Assert.Equal(merged, AiSettingsNodeType.MergePackageSources(merged, "Office"));
        // A user on legacy templates keeps the legacy behavior byte for byte.
        var legacy = AiSettingsNodeType.MergePackageSources(new AiSettings(), "Office");
        Assert.True(legacy.SkillSources.IsDefaultOrEmpty);
        Assert.Contains("namespace:Office/Skill nodeType:Skill", legacy.SkillQueries);
    }

    [Fact]
    public void SearchHelpers_AnchorAndNarrow()
    {
        Assert.Equal("ACME", AiSourceCatalog.AnchorNamespace("namespace:ACME/Skill|Skill nodeType:Skill".Replace("ACME/Skill", "ACME")));
        Assert.Equal("Office", AiSourceCatalog.AnchorNamespace("path:Office scope:descendants nodeType:Skill"));
        Assert.Null(AiSourceCatalog.AnchorNamespace("nodeType:Skill"));
        Assert.Equal("namespace:Provider nodeType:LanguageModel scope:descendants",
            AiSourceCatalog.ForSearch("namespace:Provider nodeType:LanguageModel|ModelProvider scope:descendants select:path,id,content",
                LanguageModelNodeType.NodeType));
    }
}
