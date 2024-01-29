using OpenSmc.Domain.Abstractions.Attributes;

namespace OpenSmc.TestDomain.Scopes
{
    public record YearQuarterAndCompany
    {
        [Dimension(typeof(int), nameof(Year))]
        public int Year { get; init; }

        [Dimension(typeof(int), nameof(Quarter))]
        public int Quarter { get; init; }

        [Dimension(typeof(Company))]
        public string Company { get; init; }
    }
}