namespace OpenSmc.Northwind.Host;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        await using var app = builder.Build();

        await app.RunAsync();
    }
}
