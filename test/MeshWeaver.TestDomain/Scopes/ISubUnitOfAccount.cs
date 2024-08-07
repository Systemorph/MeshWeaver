using System.ComponentModel.DataAnnotations;
using MeshWeaver.Arithmetics.Aggregation;
using MeshWeaver.Domain;
using MeshWeaver.Scopes;

namespace MeshWeaver.TestDomain.Scopes;

public interface ISubUnitOfAccount : IScope<DataNode, UnitOfAccountStorage>
{
    IUnitOfAccount UnitOfAccount =>
        GetScope<IUnitOfAccount>(new DataNode { SystemName = Identity.Parent });
    IBestEstimate BestEstimate => GetScope<IBestEstimate>();
    IRiskAdjustment RiskAdjustment => GetScope<IRiskAdjustment>();
    IContractualServiceMargin ContractualServiceMargin => GetScope<IContractualServiceMargin>();
}

public interface IUnitOfAccount : IScope<DataNode, UnitOfAccountStorage>
{
    ISubUnitOfAccount SubUnits =>
        GetScope<ISubUnitOfAccount>(
            Identity,
            o =>
                o.WithFactory(
                    () =>
                        GetStorage()
                            .GetIdentitiesByParent(Identity.SystemName)
                            .Select(GetScope<ISubUnitOfAccount>)
                            .Aggregate()
                )
        );

    IUnitOfAccountContractualServiceMargin ContractualServiceMargin =>
        GetScope<IUnitOfAccountContractualServiceMargin>();
}

public interface IFxConversion : IScope<DataNode, UnitOfAccountStorage>
{
    static ApplicabilityBuilder FxConversionApplicability(ApplicabilityBuilder builder) =>
        builder.ForScope<IFxConversion>(s =>
            s.WithApplicability<IFunctionalFxConversion>(
                    x => x.Identity.FxType == FxType.Functional,
                    p => p.ForMember(x => x.FxMid)
                )
                .WithApplicability<IGroupFxConversion>(x =>
                    x.Identity.FxType == FxType.Group || x.Identity.FxType == FxType.Functional
                )
        );

    double FxBoP => 1;
    double FxMid => 1;
    double FxEoP => 1;
}

public interface IGroupFxConversion : IFxConversion
{
    double IFxConversion.FxBoP => GetStorage().GetFx(Identity, FxTime.BoP);
    double IFxConversion.FxMid => GetStorage().GetFx(Identity, FxTime.Mid);
    double IFxConversion.FxEoP => GetStorage().GetFx(Identity, FxTime.EoP);
}

public interface IFunctionalFxConversion : IGroupFxConversion
{
    double IFxConversion.FxMid => FxBoP;
}

public interface IBestEstimate : IFxConversion
{
    ISubUnitOfAccount ParentScope => GetScope<ISubUnitOfAccount>();

    static ApplicabilityBuilder Applicability(ApplicabilityBuilder builder) =>
        builder.ForScope<IBestEstimate>(s =>
            s.WithApplicability<IBestEstimateMethod1>(
                    be => be.Identity.Method == 1,
                    part => part.ForMember(be => be.Delta)
                )
                .WithApplicability<IBestEstimateMethod2>(
                    be => be.Identity.Method == 2,
                    part => part.ForMember(be => be.Delta)
                )
        );

    [NotVisible]
    double BoPInTransactionalCurrency => GetStorage().GetBestEstimateBoP(Identity);

    [Display(Name = "Beginning of Period")]
    double BoP => FxBoP * BoPInTransactionalCurrency;

    [NotVisible]
    double DeltaInTransactionalCurrency => GetStorage().GetBestEstimateDelta(Identity);

    double Delta => FxMid * DeltaInTransactionalCurrency;

    [Display(Name = "End of Period")]
    double EoP => BoP + Delta;

    double FxResidual => EoP - FxEoP * (Delta / FxMid + BoP / FxBoP);
}

public interface IBestEstimateMethod1 : IBestEstimate
{
    double IBestEstimate.Delta => FxMid * Math.Abs(DeltaInTransactionalCurrency);
}

public interface IBestEstimateMethod2 : IBestEstimate
{
    double IBestEstimate.Delta => -1 * FxMid * Math.Abs(DeltaInTransactionalCurrency);
}

public interface IRiskAdjustment : IScope<DataNode, UnitOfAccountStorage>
{
    [Display(Name = "Beginning of Period")]
    double BoP => GetStorage().GetRiskAdjustmentBoP(Identity);

    double Delta => GetStorage().GetRiskAdjustmentDelta(Identity);

    [Display(Name = "End of Period")]
    double EoP => BoP + Delta;
}

public interface IUnitOfAccountContractualServiceMargin : IScope<DataNode, UnitOfAccountStorage>
{
    static ApplicabilityBuilder Applicability(ApplicabilityBuilder builder) =>
        builder.ForScope<IUnitOfAccountContractualServiceMargin>(s =>
            s.WithApplicability<IProfitableContractualServiceMargin>(
                    x => x.IsProfitable,
                    part => part.ForMember(p => p.Amortization)
                )
                .WithApplicability<IOnerousContractualServiceMargin>(
                    x => !x.IsProfitable,
                    part => part.ForMember(p => p.LossComponentReversal)
                )
        );

    IUnitOfAccount ParentScope => GetScope<IUnitOfAccount>();

    [Display(Name = "Beginning of Period")]
    double BoP => ParentScope.SubUnits.ContractualServiceMargin.BoP;

    double Delta =>
        ParentScope.SubUnits.BestEstimate.Delta + ParentScope.SubUnits.RiskAdjustment.Delta;

    [NotVisible]
    double EoPTemp => BoP + Delta;

    [NotVisible]
    bool IsProfitable => EoPTemp > 0;

    double Amortization => 0;
    double LossComponentReversal => 0;

    [Display(Name = "End of Period")]
    double EoP => EoPTemp + Amortization + LossComponentReversal;
}

public interface IProfitableContractualServiceMargin : IUnitOfAccountContractualServiceMargin
{
    double AmortizationFactor => 0.1;
    double IUnitOfAccountContractualServiceMargin.Amortization => -EoPTemp * AmortizationFactor;
}

public interface IOnerousContractualServiceMargin : IUnitOfAccountContractualServiceMargin
{
    double AmortizationFactor => 0.1;
    double IUnitOfAccountContractualServiceMargin.LossComponentReversal =>
        -EoPTemp * AmortizationFactor;
}

public interface IContractualServiceMargin : IScope<DataNode, UnitOfAccountStorage>
{
    ISubUnitOfAccount ParentScope => GetScope<ISubUnitOfAccount>();

    [Display(Name = "Beginning of Period")]
    double BoP => GetStorage().GetContractualServiceMarginBoP(Identity);

    double Delta =>
        ParentScope.UnitOfAccount.ContractualServiceMargin.Delta
        * Weight
        / ParentScope.UnitOfAccount.SubUnits.ContractualServiceMargin.Weight;

    double Amortization =>
        ParentScope.UnitOfAccount.ContractualServiceMargin.Amortization
        * Weight
        / ParentScope.UnitOfAccount.SubUnits.ContractualServiceMargin.Weight;
    double LossComponentReversal =>
        ParentScope.UnitOfAccount.ContractualServiceMargin.LossComponentReversal
        * Weight
        / ParentScope.UnitOfAccount.SubUnits.ContractualServiceMargin.Weight;

    [Display(Name = "End of Period")]
    double EoP => BoP + Delta + Amortization + LossComponentReversal;

    double Weight => Math.Abs(BoP);
}

public class UnitOfAccountStorage
{
    private readonly Dictionary<string, DataNode[]> identitiesByParent;
    private static readonly Random Random = new(20210531);
    public ICollection<DataNode> Identities { get; }

    public UnitOfAccountStorage(int nScopes, Func<int, int> parent)
    {
        Identities = Enumerable
            .Range(1, nScopes)
            .Select(i => new DataNode
            {
                SystemName = i.ToString(),
                Parent = parent(i).ToString(),
                Method = parent(i)
            })
            .ToArray();
        identitiesByParent = Identities
            .GroupBy(x => x.Parent)
            .ToDictionary(x => x.Key, x => x.ToArray());
    }

    public IEnumerable<DataNode> GetIdentitiesByParent(string parent)
    {
        return identitiesByParent[parent];
    }

    public double GetBestEstimateBoP(DataNode identity) => Random.NextDouble();

    public double GetBestEstimateDelta(DataNode identity) => 2 * Random.NextDouble() - 1;

    public double GetRiskAdjustmentBoP(DataNode identity) => Random.NextDouble();

    public double GetContractualServiceMarginBoP(DataNode identity) => Random.NextDouble();

    public double GetRiskAdjustmentDelta(DataNode identity) => 2 * Random.NextDouble() - 1;

    private static readonly Dictionary<FxTime, double> FxRates = Enum.GetValues(typeof(FxTime))
        .Cast<FxTime>()
        .ToDictionary(x => x, _ => Random.NextDouble());

    public double GetFx(DataNode identity, FxTime time) => FxRates[time];
}

public record DataNode
{
    public string SystemName { get; init; }
    public string Parent { get; init; }
    public FxType FxType { get; init; }
    public int Method { get; init; }
}

public enum FxType
{
    Transactional,
    Functional,
    Group
}

public enum FxTime
{
    BoP,
    Mid,
    EoP
}
