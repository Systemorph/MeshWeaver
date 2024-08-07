using System.ComponentModel;

namespace MeshWeaver.Scopes.Test;

public interface IMainScope : IMutableScope
{
    int TotalValue => Enumerable.Range(0, GetScope<IDummySimulationParams>().NSimulations).Sum(i => GetScope<IDummySimulationScope>(new DummySimulatorIdentity(i)).Value);

    void IncrementCoefficient()
    {
        GetScope<IDummySimulationParams>().Coefficient += 1;
    }
}

public record SingletonScenarioTestStorage;
public record DummySimulatorIdentity(int Index);

public interface IDummySimulationScope : IMutableScope<DummySimulatorIdentity, SingletonScenarioTestStorage>
{
    int Value => GetScope<IDummySimulationParams>().Coefficient * Identity.Index;
}

public interface IDummySimulationParams : IMutableScope
{
    const int DefaultCoefficient = 1;
    [DefaultValue(DefaultCoefficient)]
    int Coefficient { get; set; }

    const int DefaultNSimulations = 5;
    [DefaultValue(DefaultNSimulations)]
    int NSimulations { get; set; }
}