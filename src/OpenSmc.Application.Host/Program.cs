using OpenSmc.Application.SignalR;

namespace OpenSmc.Application.Host;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.ConfigureApplicationSignalR();

        using var app = builder.Build();

        app
            .UseRouting()
            .UseApplicationSignalR();

        await app.RunAsync();
    }
}
