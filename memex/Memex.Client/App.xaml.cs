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

    // Native MAUI shell: the instance manager is the landing page (no BlazorWebView). The in-process local
    // portal renders natively via the MeshWeaver.Maui view pack (LayoutAreaView), wired into a page next wave.
    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new NavigationPage(_services.GetRequiredService<InstanceManagerPage>())) { Title = "Memex" };
}
