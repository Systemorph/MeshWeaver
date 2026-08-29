using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Layout.Catalog;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The catalog config flags default to <c>true</c> in C# but to <c>false</c> in the CLR, and the hub
/// serializes with <see cref="JsonIgnoreCondition.WhenWritingDefault"/> — which compares against the
/// CLR default. So an explicit <c>false</c> was DROPPED and the reader re-applied the initializer:
/// the flags could be turned on, never off.
///
/// <para>Measured 2026-08-29 on MeshWeaver.Crm's activity feed: it asked for
/// <c>Ascending = false</c> (newest first) and the payload carried no <c>ascending</c> at all, so
/// the portal rendered oldest-first. Nothing errored — the intent simply evaporated between the two
/// sides.</para>
/// </summary>
public class CatalogConfigFlagsSurviveTheWireTest
{
    // The hub's shape: default-valued members are not written.
    private static readonly JsonSerializerOptions HubLike = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    [Fact]
    public void SortConfig_descending_survives_a_round_trip()
    {
        var sort = new SortConfig { SortByProperty = "date", Ascending = false, ThenByAscending = false };

        var json = JsonSerializer.Serialize(sort, HubLike);
        Assert.Contains("\"ascending\":false", json);
        Assert.Contains("\"thenByAscending\":false", json);

        Assert.False(JsonSerializer.Deserialize<SortConfig>(json, HubLike)!.Ascending);
    }

    [Fact]
    public void SectionConfig_off_switches_survive_a_round_trip()
    {
        var sections = new SectionConfig { ShowCounts = false, Collapsible = false };

        var json = JsonSerializer.Serialize(sections, HubLike);
        Assert.Contains("\"showCounts\":false", json);
        Assert.Contains("\"collapsible\":false", json);

        var read = JsonSerializer.Deserialize<SectionConfig>(json, HubLike)!;
        Assert.False(read.ShowCounts);
        Assert.False(read.Collapsible);
    }

    [Fact]
    public void The_true_case_and_the_defaults_are_unchanged()
    {
        Assert.True(JsonSerializer.Deserialize<SortConfig>(
            JsonSerializer.Serialize(new SortConfig { Ascending = true }, HubLike), HubLike)!.Ascending);

        // An absent flag still reads as the documented default — nothing about existing payloads moves.
        Assert.True(JsonSerializer.Deserialize<SortConfig>("""{"sortByProperty":"name"}""", HubLike)!.Ascending);
    }
}
