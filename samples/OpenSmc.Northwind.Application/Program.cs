using System.Diagnostics;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenSmc.Hosting;
using OpenSmc.Northwind.Application;
using OpenSmc.Northwind.Application.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddFluentUIComponents();
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddLogging(config => config.AddSimpleConsole(
    options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "hh:mm:ss:fff";
        options.IncludeScopes = true;
    }).AddDebug());
builder.Services.AddFluentUIComponents();

builder.Host.UseOpenSmc(
    new BlazorServerAddress(),
    config => config.ConfigureNorthwindHubs()
);

if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting blazor server on PID: {PID}", Process.GetCurrentProcess().Id);

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. Yoseru may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.UseHttpsRedirection();
app.UseStaticFiles();
app.Run();

public record BlazorServerAddress
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
