namespace MeshWeaver.TestDomain.Scopes
{
    public class YearAndQuarterAndCompanyIdentityStorage
    {
        public YearAndQuarterAndCompanyIdentityStorage(params (int year, int quarter)[] tuples)
        {
            Identities = CreateIdentitites(tuples).ToArray();
        }

        private IEnumerable<YearQuarterAndCompany> CreateIdentitites((int year, int quarter)[] tuples)
        {
            foreach (var (year, quarter) in tuples)
            {
                foreach (var company in Company.Data)
                {
                    yield return new()
                                 {
                                     Company = company.SystemName,
                                     Year = year,
                                     Quarter = quarter
                                 };
                }
            }
        }

        public ICollection<YearQuarterAndCompany> Identities { get; }
    }
}