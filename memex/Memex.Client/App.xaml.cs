namespace Memex.Client;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Single BlazorWebView page. (A TabbedPage wrapper makes BlazorWebView render blank on
		// several platforms, so the portal is opened in an in-app browser from a Blazor page instead.)
		return new Window(new MainPage()) { Title = "Memex" };
	}
}
