using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;

namespace OpenSmc.TestDomain.SimpleData;

public record SimpleAccounting
{
    [Display(Order = 1)]
    public double Delta { get; init; }

    [Display(Order = 0, Name = "Beginning of Period")]
    public double BoP { get; init; }

    [Display(Name = "End of Period")]
    public double EoP => BoP + Delta;

    [NotVisible]
    public string SystemName { get; init; }

    [NotVisible]
    public string DisplayName { get; init; }
}

public record SimpleAccountingNamed : SimpleAccounting, INamed;

public static class SimpleAccountingFactory
{
    public static SimpleAccounting[] GetData(int n)
    {
        return Enumerable
            .Range(0, n)
            .Select(i => new SimpleAccounting
            {
                Delta = i,
                SystemName = $"i{i + 1}",
                DisplayName = $"Item {i + 1}"
            })
            .ToArray();
    }

    public static TRecord[] GetData<TRecord>(int n)
        where TRecord : SimpleAccounting, new()
    {
        return Enumerable
            .Range(0, n)
            .Select(i => new TRecord
            {
                Delta = i,
                SystemName = $"i{i + 1}",
                DisplayName = $"Item {i + 1}"
            })
            .ToArray();
    }
}
