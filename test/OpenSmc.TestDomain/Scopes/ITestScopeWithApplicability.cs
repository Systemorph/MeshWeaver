using OpenSmc.Scopes;

namespace OpenSmc.TestDomain.Scopes
{
    public interface ITestScopeWithApplicability : IScope<MethodIdentity, MethodIdentityStorage>
    {
        double BoP => 0;
        double Delta => 2;

        double EoP => BoP + Delta;

        double Profit => 0;
        double GetProfit() => 0;
        double Loss => 0;
        double GetLoss() => 0;

        bool IsProfitable => Identity.Method > 0; //Uoa.IsProfitable
    }

    public interface ITestScopeWithCyclicMethodResolutionApplicability : ITestScopeWithApplicability
    {
        public static ApplicabilityBuilder Applicability(
            ApplicabilityBuilder builder) =>
            builder.ForScope<ITestScopeWithCyclicMethodResolutionApplicability>(scope =>
                                                                                    scope
                                                                                        .WithApplicability<IScopeMethodProfitable>(
                                                                                                                                   x => x.IsProfitable // This generates a cyclic dependency as this rule is evaluated when resolving the IsProfitable getter itself.
                                                                                                                                   //, options =>
                                                                                                                                   //    options.ForMember(s => s.Profit) // such statements would avoid cyclic resolution, but we cannot assume that users put them
                                                                                                                                   //        .ForMember(s => s.GetProfit()) // such statements would avoid cyclic resolution, but we cannot assume that users put them
                                                                                                                                  )
                                                                                        .WithApplicability<IScopeMethodOnerous>(x => !x.IsProfitable
                                                                                                                                //, options =>
                                                                                                                                //    options.ForMember(s => s.Loss)
                                                                                                                                //        .ForMember(s => s.GetLoss())
                                                                                                                               ));
    }

    public interface ITestScopeWithComplexApplicability : ITestScopeWithApplicability
    {
        public static ApplicabilityBuilder Applicability(ApplicabilityBuilder builder) =>
            builder.ForScope<ITestScopeWithComplexApplicability>(scope =>
                                                                     scope.WithApplicability<IScopeMethodProfitable>(
                                                                                                                     x => x.IsProfitable // the IScopeMethodProfitable is used when IsProfitable is true
                                                                                                                     , options =>
                                                                                                                           options.ForMember(s => s.Profit) // applies to property Profit
                                                                                                                                  .ForMember(s => s.GetProfit()) // and to function GetProfit()
                                                                                                                    )
                                                                          .WithApplicability<IScopeMethodOnerous>(x => !x.IsProfitable
                                                                                                                  , options =>
                                                                                                                        options.ForMember(s => s.Loss)
                                                                                                                               .ForMember(s => s.GetLoss())
                                                                                                                 )
                                                                          .WithApplicability<IScopeMethod0>(x => x.Identity.Method == 0)
                                                                          .WithApplicability<IScopeMethod1>(x => x.Identity.Method == 1));
    }

    public interface IScopeMethod0 : ITestScopeWithComplexApplicability
    {
        double ITestScopeWithApplicability.Delta => 0;
    }

    public interface IScopeMethod1 : ITestScopeWithComplexApplicability
    {
        double ITestScopeWithApplicability.Delta => 1;
    }

    public interface IScopeMethodProfitable : ITestScopeWithComplexApplicability
    {
        double ITestScopeWithApplicability.Profit => 1;
        double ITestScopeWithApplicability.GetProfit() => 1;
    }

    public interface IScopeMethodOnerous : ITestScopeWithComplexApplicability
    {
        double ITestScopeWithApplicability.Loss => 1;
        double ITestScopeWithApplicability.GetLoss() => 1;
    }

    public record MethodIdentity
    {
        public int Method { get; init; }
    }

    public class MethodIdentityStorage
    {
        public IEnumerable<MethodIdentity> Identities { get; }

        public MethodIdentityStorage(int nMetghods)
        {
            Identities = Enumerable.Range(0, nMetghods).Select(i => new MethodIdentity { Method = i }).ToArray();
        }
    }
}