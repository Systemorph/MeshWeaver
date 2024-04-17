namespace OpenSmc.Application.Orleans;

public static class TestUiIds
{
    public const string HardcodedUiId = "MyTestApp"; // HACK V10: get rid of this hardcoding (2024/04/17, Dmitry Kalabin)
}

// HACK V10: move these constants to the test level (2024/04/17, Dmitry Kalabin)
public static class TestApplication
{
    public const string Name = "testApp";
    public const string Environment = "dev";
}
