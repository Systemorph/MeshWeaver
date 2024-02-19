using OpenSmc.Arithmetics;

namespace OpenSmc.Scopes.Test;
public record GuidIdentity
{
    public Guid Id { get; } = Guid.NewGuid();
}

public record IdentitiesStorage
{
    public IList<GuidIdentity> Identities { get; }

    public IdentitiesStorage(int nIdentities)
    {
        Identities = Enumerable.Range(0, nIdentities).Select(_ => new GuidIdentity()).ToArray();
    }
}

public interface IRandomScope : IScope<GuidIdentity, IdentitiesStorage>
{
    private static readonly Random Rng = new();

    /// <summary>
    /// Don't do this at home! Variables must always be deterministic and should not be volatile.
    /// This is to test actual equality of scopes.
    /// </summary>
    public double RandomVariable => Rng.NextDouble();

    /// <summary>
    /// Functions are OK to be volatile.
    /// </summary>
    /// <returns></returns>
    public double Random() => Rng.NextDouble();

    public double RandomReference => RandomVariable;
    public GuidIdentity MyIdentity => Identity;

    [DirectEvaluation]
    public bool IsBiggerThanHalf => RandomVariable > 0.5;

    [NotAggregated]
    [NoArithmetics]
    public double Ratio => RandomVariable / RandomReference;
}