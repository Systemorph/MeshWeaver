namespace Memex.Client;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    // Local-first: the app IS the portal, rendered in-process against the local mesh. The single
    // BlazorWebView page hosts the MeshWeaver portal (ApplicationPage → layout areas); instance
    // management lives inside the mesh (MemexInstance nodes), not a native shell.
    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new MainPage()) { Title = "Memex" };
}
