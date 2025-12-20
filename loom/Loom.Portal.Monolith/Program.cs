using Loom.Portal.Shared;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Messaging;

var builder = WebApplication.CreateBuilder(args);

// Add Blazor (from template)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add Loom additional services (Radzen, SignalR, etc.)
builder.AddLoomAdditionalServices();

// Configure MeshWeaver mesh
builder.UseMeshWeaver(
    AddressExtensions.CreateMeshAddress(),
    config => config
        .ConfigureLoomPortal()
        .ConfigureLoomMesh(builder.Configuration)
        .UseMonolithMesh()
);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

// Static files middleware must run before routing to serve _content/* paths from RCLs
app.UseStaticFiles();

app.UseRouting();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<Loom.Portal.Shared.App>()
    .AddMeshViews()
    .AddInteractiveServerRenderMode();

app.Run();
