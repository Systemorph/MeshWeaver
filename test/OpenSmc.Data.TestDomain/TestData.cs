namespace OpenSmc.Data.TestDomain;

public static class TestData
{
    public static readonly TransactionalData[] TransactionalData =
    {
        new("1",2024, "1", "1", 7),
        new("2", 2024, "1", "3", 2),
    };

    public static readonly LineOfBusiness[] LinesOfBusiness =
        [new("1", "1"), new("2", "2")];
    public static readonly BusinessUnit[] BusinessUnits =
        [new("1", "1"), new("2", "2")];
}