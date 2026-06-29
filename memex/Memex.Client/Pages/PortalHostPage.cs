using Memex.Client.Services;

namespace Memex.Client.Pages;

/// <summary>
/// Loads one memex instance's portal (memex-primary) in a native WebView. The user can switch to
/// another instance from here, or go back to the manager.
///
/// <para>TODO (the bridge): a mic button rendered <b>inside the portal</b> calls back to native via a
/// JS↔native channel (the same mechanism the Claude Code harness uses) — native records + transcribes
/// on-device (Whisper), then pushes the transcript back into the portal's chat. Voice is a feature
/// inside memex, not a shell around it.</para>
/// </summary>
public sealed class PortalHostPage : ContentPage
{
    private readonly InstanceStore _store;
    private readonly WebView _web;
    private MemexInstance _current;

    public PortalHostPage(InstanceStore store, MemexInstance instance)
    {
        _store = store;
        _current = instance;
        Title = instance.Name;

        _web = new WebView
        {
            Source = instance.Url,
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
        };
        Content = _web;

        ToolbarItems.Add(new ToolbarItem { Text = "⇄ Switch", Command = new Command(async () => await SwitchAsync()) });
    }

    private async Task SwitchAsync()
    {
        var others = _store.Instances;
        if (others.Count <= 1)
        {
            await DisplayAlert("Instances", "Add more instances from the manager (back).", "OK");
            return;
        }

        var names = others.Select(i => i.Name).ToArray();
        var pick = await DisplayActionSheet("Switch instance", "Cancel", null, names);
        var chosen = others.FirstOrDefault(i => i.Name == pick);
        if (chosen is not null && !ReferenceEquals(chosen, _current))
        {
            _current = chosen;
            Title = chosen.Name;
            _web.Source = chosen.Url;
        }
    }
}
