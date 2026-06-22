namespace Memex.Client;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Two tabs: the native app (in-process Blazor — Voice/Mesh) and the full portal (WebView).
		var root = new TabbedPage { Title = "Memex" };
		root.Children.Add(new MainPage { Title = "App" });
		root.Children.Add(new PortalPage());
		return new Window(root) { Title = "Memex" };
	}
}
