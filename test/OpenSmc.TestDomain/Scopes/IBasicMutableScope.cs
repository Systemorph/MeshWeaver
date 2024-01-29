using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Scopes;

namespace OpenSmc.TestDomain.Scopes
{
    public interface IBasicMutableScope : IMutableScope<string, IdentitiesStorage>
    {
        [Range(1,2)]
        public int SettablePropertyWithRange { get; set; }

        [DefaultValue(1)]
        public int SettablePropertyWithDefaultAttribute { get; set; }
        public int SettableProperty { get; set; }
        public int DependentProperty => SettableProperty + 1;
        public int SecondDependentProperty => DependentProperty + 1;
    }

    public interface IComplexMutableScope : IMutableScope<string, IdentitiesStorage>
    {
        IBasicMutableScope Basic1 => GetScope<IBasicMutableScope>(Identity, o => o.WithContext("1"));
        IBasicMutableScope Basic2 => GetScope<IBasicMutableScope>(Identity, o => o.WithContext("2"));
        int Combined => Basic1.SecondDependentProperty + Basic2.SecondDependentProperty;
        // ReSharper disable once IntDivisionByZero
        int Combined2 => Basic1.SecondDependentProperty + Basic2.SecondDependentProperty;
    }

    public interface IMutableScopeWithProblematicProperties : IComplexMutableScope
    {
        int CombinedWithException => (Basic1.SecondDependentProperty + Basic2.SecondDependentProperty) / 0;
    }

    public interface IEdgeCasePropertiesMutableScope : IMutableScope<string, IdentitiesStorage>
    {
        public int? SettableNullableIntProperty { get; set; }
    }

    public interface IDependencyWithNonChangingProperty : IMutableScope<string>
    {

        public int Value { get; set; }

        public int ValueDividedBy2 => Value / 2;

        public int ValuePlus1DividedBy2 => (Value+1)/2;
    }

}

