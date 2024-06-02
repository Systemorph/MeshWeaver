using Microsoft.FluentUI.AspNetCore.Components;
using OpenSmc.Application;
using OpenSmc.Hosting;
using OpenSmc.Northwind.Application;
using OpenSmc.Northwind.Application.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor()
// .AddHubOptions(o =>
// {
//     o.MaximumReceiveMessageSize = 10 * 1024 * 1024;
// })
// .AddJsonProtocol(options =>
// {
//     options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
//     options.PayloadSerializerOptions.WriteIndented = true;
// })
;
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddLogging(config => config.AddConsole().AddDebug());
builder.Services.AddFluentUIComponents(); 
builder.Host.UseOpenSmc(
    new ApplicationAddress("Northwind", "dev"),
    config => config.ConfigureNorthwindHubs()
);
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. Yoseru may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
