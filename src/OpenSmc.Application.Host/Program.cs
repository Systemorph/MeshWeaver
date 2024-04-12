namespace OpenSmc.Application.Host;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        using var app = builder.Build();

        await app.RunAsync();
    }
}
