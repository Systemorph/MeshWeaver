namespace OpenSmc.TestDomain.Cubes
{
    public static class CashflowFactory
    {
        private static readonly Random Rng = new Random(07052021);

        public static IEnumerable<CashflowElement> GenerateRandom(int n)
        {
            return Enumerable.Range(0, n).Select(_ => Create());
        }

        public static IEnumerable<CashflowElement> GenerateRandomSingleCurrency(int n)
        {
            return Enumerable.Range(0, n).Select(_ => CreateSingleCurrency());
        }

        public static IEnumerable<CashflowElement> GenerateEquallyWeightedAllPopulated()
        {
            foreach (var lob in LineOfBusiness.Data)
            foreach (var country in Country.Data)
            foreach (var amountType in AmountType.Data)
            foreach (var scenario in Scenario.Data)
            foreach (var split in Split.Data)
            foreach (var currency in Currency.Data)
                yield return new CashflowElement()
                             {
                                 LineOfBusiness = lob.SystemName,
                                 Country = country.SystemName,
                                 AmountType = amountType.SystemName,
                                 Scenario = scenario.SystemName,
                                 Split = split.SystemName,
                                 Currency = currency.SystemName,
                                 Value = 1
                             };
        }

        public static IEnumerable<CashflowElement> GenerateEquallyWeightedAllPopulatedWithNull()
        {
            foreach (var lob in LineOfBusiness.Data.Select(x=>x.SystemName).Append(null))
            foreach (var country in Country.Data.Select(x=>x.SystemName).Append(null))
            foreach (var amountType in AmountType.Data.Select(x=>x.SystemName).Append(null))
            foreach (var scenario in Scenario.Data.Select(x=>x.SystemName).Append(null))
            foreach (var split in Split.Data.Select(x=>x.SystemName).Append(null))
            foreach (var currency in Currency.Data.Select(x=>x.SystemName).Append(null))
                yield return new CashflowElement()
                             {
                                 LineOfBusiness = lob,
                                 Country = country,
                                 AmountType = amountType,
                                 Scenario = scenario,
                                 Split = split,
                                 Currency = currency,
                                 Value = 1
                             };
        }
        
        public static IEnumerable<CashflowElement> GenerateEquallyWeightedAllPopulatedSingleCurrency()
        {
            foreach (var lob in LineOfBusiness.Data)
            foreach (var country in Country.Data)
            foreach (var amountType in AmountType.Data)
            foreach (var scenario in Scenario.Data)
            foreach (var split in Split.Data)
                yield return new CashflowElement()
                             {
                                 LineOfBusiness = lob.SystemName,
                                 Country = country.SystemName,
                                 AmountType = amountType.SystemName,
                                 Scenario = scenario.SystemName,
                                 Split = split.SystemName,
                                 Currency = Currency.Data[0].SystemName,
                                 Value = 1
                             };
        }

        private static CashflowElement Create()
        {
            return new()
                   {
                       LineOfBusiness = LineOfBusiness.Data[Rng.Next(LineOfBusiness.Data.Length)].SystemName,
                       Country = Country.Data[Rng.Next(Country.Data.Length)].SystemName,
                       AmountType = AmountType.Data[Rng.Next(AmountType.Data.Length)].SystemName,
                       Scenario = Scenario.Data[Rng.Next(Scenario.Data.Length)].SystemName,
                       Split = Split.Data[Rng.Next(Split.Data.Length)].SystemName,
                       Currency = Currency.Data[Rng.Next(Currency.Data.Length)].SystemName,
                       Value = Rng.NextDouble()
                   };
        }

        private static CashflowElement CreateSingleCurrency()
        {
            return new()
                   {
                       LineOfBusiness = LineOfBusiness.Data[Rng.Next(LineOfBusiness.Data.Length)].SystemName,
                       Country = Country.Data[Rng.Next(Country.Data.Length)].SystemName,
                       AmountType = AmountType.Data[Rng.Next(AmountType.Data.Length)].SystemName,
                       Scenario = Scenario.Data[Rng.Next(Scenario.Data.Length)].SystemName,
                       Split = Split.Data[Rng.Next(Split.Data.Length)].SystemName,
                       Currency = Currency.Data[0].SystemName,
                       Value = Rng.NextDouble()
                   };
        }

        public static IEnumerable<CashflowElementWithInt> GenerateEquallyWeightedAllPopulatedWithInt()
        {
            foreach (var lob in LineOfBusiness.Data)
            foreach (var country in Country.Data)
            foreach (var amountType in AmountType.Data)
            foreach (var scenario in Scenario.Data)
            foreach (var split in Split.Data)
            foreach (var year in new[] { 2022, 2023, 2024 })
                yield return new CashflowElementWithInt()
                             {
                                 LineOfBusiness = lob.SystemName,
                                 Country = country.SystemName,
                                 AmountType = amountType.SystemName,
                                 Scenario = scenario.SystemName,
                                 Split = split.SystemName,
                                 Currency = Currency.Data[0].SystemName,
                                 Year = year,
                                 Value = 1
                             };
        }
    }
}