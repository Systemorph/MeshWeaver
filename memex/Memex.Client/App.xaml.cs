using Memex.Client.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Client;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
    }

    // Native MAUI shell: a browser-like PortalShellPage (its own back/forward history + an instance switcher
    // in the title bar) over a content frame. The in-process local portal renders natively via the
    // MeshWeaver.Maui view pack (LayoutAreaView) — no BlazorWebView. The shell IS the navigator, so it's the
    // window root directly (no NavigationPage wrapper).
    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(_services.GetRequiredService<PortalShellPage>()) { Title = "Memex" };
}
