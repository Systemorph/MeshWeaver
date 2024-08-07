using System.ComponentModel;
using MeshWeaver.Scopes;

namespace MeshWeaver.TestDomain.Scopes
{
    public interface IMutableScopeWithCustomGetSet : IMutableScope<GuidIdentity, IdentitiesStorage>
    {
        [DefaultValue(2020)]
        int Year { get; set; }
        
        [DefaultValue(1)]
        int PeriodValue { get; set; }
        [DefaultValue("Q")]
        string Periodicity { get; set; }

        string Period
        {
            get => $"{Periodicity}{PeriodValue}";
            set
            {
                Periodicity = value.Substring(0, 1);
                PeriodValue = int.Parse(value.Substring(1));
            }
        }

        string Partition
        {
            get => $"{Year}/{Period}";
            set
            {
                var split = value.Split('/');

                if (split.Length != 2)
                    throw new ArgumentException($"Wrong value {value}");

                Year = int.Parse(split[0]);
                Period = split[1];

                var validReportingNodes = AvailableResetReportingNodesByYear(Year);
                if (!validReportingNodes.Contains(ReportingNode))
                {
                    ReportingNode = validReportingNodes.First();
                }
            }
        }

        [DefaultValue("RN2020")]
        string ReportingNode { get; set; }

        static IReadOnlyCollection<string> AvailableResetReportingNodesByYear(int year)
        {
            return year switch
            {
                >= 2020 and <= 3000 => new []{ "RN2020", "RN2021" , "RN2022" },
                _ => new[] { "RN2019", "RN2018" }
            };
        }

        string ApplicabilitySwitch { get; set; }

        // ReSharper disable once UnusedMember.Global
        static ApplicabilityBuilder ApplicabilityBuilder(ApplicabilityBuilder builder)
        {
            return builder
                .ForScope<IMutableScopeWithCustomGetSet>
                    (s => s.WithApplicability<IMutableScopeWithCustomGetSetOverridePartition>(x => x.ApplicabilitySwitch == nameof(IMutableScopeWithCustomGetSetOverridePartition))
                     );
        }

    }

    public interface IMutableScopeWithCustomGetSetOverridePartition : IMutableScopeWithCustomGetSet
    {
        string IMutableScopeWithCustomGetSet.Partition
        {
            get => $"{Year}-{Period}";
            set
            {
                var split = value.Split('-');

                if (split.Length != 2)
                    throw new ArgumentException($"Wrong value {value}");

                Year = int.Parse(split[0]);
                Period = split[1];
            }
        }

    }
}