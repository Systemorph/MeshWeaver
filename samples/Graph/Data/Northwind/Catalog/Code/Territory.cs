// <meshweaver>
// Id: Territory
// DisplayName: Territory Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Sales territory reference data.
/// </summary>
public record Territory
{
    [Key]
    public int TerritoryId { get; init; }

    public string TerritoryDescription { get; init; } = string.Empty;

    [Dimension(typeof(Region))]
    public int RegionId { get; init; }

    public static readonly Territory[] All =
    [
        new() { TerritoryId = 01581, TerritoryDescription = "Westboro", RegionId = 1 },
        new() { TerritoryId = 01730, TerritoryDescription = "Bedford", RegionId = 1 },
        new() { TerritoryId = 01833, TerritoryDescription = "Georgetown", RegionId = 1 },
        new() { TerritoryId = 02116, TerritoryDescription = "Boston", RegionId = 1 },
        new() { TerritoryId = 02139, TerritoryDescription = "Cambridge", RegionId = 1 },
        new() { TerritoryId = 02184, TerritoryDescription = "Braintree", RegionId = 1 },
        new() { TerritoryId = 02903, TerritoryDescription = "Providence", RegionId = 1 },
        new() { TerritoryId = 03049, TerritoryDescription = "Hollis", RegionId = 3 },
        new() { TerritoryId = 03801, TerritoryDescription = "Portsmouth", RegionId = 3 },
        new() { TerritoryId = 06897, TerritoryDescription = "Wilton", RegionId = 1 },
        new() { TerritoryId = 07960, TerritoryDescription = "Morristown", RegionId = 1 },
        new() { TerritoryId = 08837, TerritoryDescription = "Edison", RegionId = 1 },
        new() { TerritoryId = 10019, TerritoryDescription = "New York", RegionId = 1 },
        new() { TerritoryId = 10038, TerritoryDescription = "New York", RegionId = 1 },
        new() { TerritoryId = 11747, TerritoryDescription = "Mellville", RegionId = 1 },
        new() { TerritoryId = 14450, TerritoryDescription = "Fairport", RegionId = 1 },
        new() { TerritoryId = 19428, TerritoryDescription = "Philadelphia", RegionId = 3 },
        new() { TerritoryId = 19713, TerritoryDescription = "Newark", RegionId = 1 },
        new() { TerritoryId = 20852, TerritoryDescription = "Rockville", RegionId = 1 },
        new() { TerritoryId = 27403, TerritoryDescription = "Greensboro", RegionId = 1 },
        new() { TerritoryId = 27511, TerritoryDescription = "Cary", RegionId = 1 },
        new() { TerritoryId = 29202, TerritoryDescription = "Columbia", RegionId = 4 },
        new() { TerritoryId = 30346, TerritoryDescription = "Atlanta", RegionId = 4 },
        new() { TerritoryId = 31406, TerritoryDescription = "Savannah", RegionId = 4 },
        new() { TerritoryId = 32859, TerritoryDescription = "Orlando", RegionId = 4 },
        new() { TerritoryId = 33607, TerritoryDescription = "Tampa", RegionId = 4 },
        new() { TerritoryId = 40222, TerritoryDescription = "Louisville", RegionId = 1 },
        new() { TerritoryId = 44122, TerritoryDescription = "Beachwood", RegionId = 3 },
        new() { TerritoryId = 45839, TerritoryDescription = "Findlay", RegionId = 3 },
        new() { TerritoryId = 48075, TerritoryDescription = "Southfield", RegionId = 3 },
        new() { TerritoryId = 48084, TerritoryDescription = "Troy", RegionId = 3 },
        new() { TerritoryId = 48304, TerritoryDescription = "Bloomfield Hills", RegionId = 3 },
        new() { TerritoryId = 53404, TerritoryDescription = "Racine", RegionId = 3 },
        new() { TerritoryId = 55113, TerritoryDescription = "Roseville", RegionId = 3 },
        new() { TerritoryId = 55439, TerritoryDescription = "Minneapolis", RegionId = 3 },
        new() { TerritoryId = 60179, TerritoryDescription = "Hoffman Estates", RegionId = 2 },
        new() { TerritoryId = 60601, TerritoryDescription = "Chicago", RegionId = 2 },
        new() { TerritoryId = 72716, TerritoryDescription = "Bentonville", RegionId = 4 },
        new() { TerritoryId = 75234, TerritoryDescription = "Dallas", RegionId = 4 },
        new() { TerritoryId = 78759, TerritoryDescription = "Austin", RegionId = 4 },
        new() { TerritoryId = 80202, TerritoryDescription = "Denver", RegionId = 2 },
        new() { TerritoryId = 80909, TerritoryDescription = "Colorado Springs", RegionId = 2 },
        new() { TerritoryId = 85014, TerritoryDescription = "Phoenix", RegionId = 2 },
        new() { TerritoryId = 85251, TerritoryDescription = "Scottsdale", RegionId = 2 },
        new() { TerritoryId = 90405, TerritoryDescription = "Santa Monica", RegionId = 2 },
        new() { TerritoryId = 94025, TerritoryDescription = "Menlo Park", RegionId = 2 },
        new() { TerritoryId = 94105, TerritoryDescription = "San Francisco", RegionId = 2 },
        new() { TerritoryId = 95008, TerritoryDescription = "Campbell", RegionId = 2 },
        new() { TerritoryId = 95054, TerritoryDescription = "Santa Clara", RegionId = 2 },
        new() { TerritoryId = 95060, TerritoryDescription = "Santa Cruz", RegionId = 2 },
        new() { TerritoryId = 98004, TerritoryDescription = "Bellevue", RegionId = 2 },
        new() { TerritoryId = 98052, TerritoryDescription = "Redmond", RegionId = 2 },
        new() { TerritoryId = 98104, TerritoryDescription = "Seattle", RegionId = 2 },
    ];
}
