using Memex.Client.Pages;
using Memex.Client.Services;

namespace Memex.Client;

public partial class App : Application
{
	private readonly InstanceStore _store;

	public App(InstanceStore store)
	{
		InitializeComponent();
		_store = store;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Start in the native instance-manager; opening an instance loads its portal (memex-primary).
		return new Window(new NavigationPage(new InstanceManagerPage(_store))) { Title = "Memex" };
	}
}
