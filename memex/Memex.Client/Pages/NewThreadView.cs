using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Memex.Client.Services;
using Microsoft.Maui.ApplicationModel;

namespace Memex.Client.Pages;

/// <summary>
/// A full-screen "new thread" page: an empty composer where the user types the first message. Send starts a
/// mesh thread (<c>hub.StartThread</c>) and opens it. Uses a native <see cref="Editor"/> (no WebView/Monaco)
/// over an opaque dark surface, so it never renders as a blank/black page.
/// </summary>
public sealed class NewThreadView : ContentView
{
    public NewThreadView(IMessageHub hub, Action<MeshNode> onCreated)
    {
        var composer = new Editor
        {
            Placeholder = "Type your first message…",
            FontSize = 16,
            TextColor = Colors.White,
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 90,
        };
        var status = new Label { IsVisible = false, TextColor = Colors.OrangeRed, FontSize = 12 };
        var send = new Button
        {
            Text = "Start thread", BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White,
            CornerRadius = 8, Padding = new Thickness(18, 8), HorizontalOptions = LayoutOptions.End,
        };
        send.Clicked += (_, _) =>
        {
            var text = composer.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            send.IsEnabled = false;
            status.IsVisible = false;
            hub.StartThread(DeviceOnboarding.DeviceUserId, text,
                onCreated: node => MainThread.BeginInvokeOnMainThread(() => onCreated(node)),
                onError: err => MainThread.BeginInvokeOnMainThread(() =>
                {
                    send.IsEnabled = true;
                    status.Text = err;
                    status.IsVisible = true;
                }));
        };

        var grid = new Grid
        {
            BackgroundColor = Color.FromArgb("#141416"),
            Padding = 24,
            RowSpacing = 12,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
        };
        grid.Add(new Label { Text = "New thread", FontSize = 22, FontAttributes = FontAttributes.Bold }, 0, 0);
        grid.Add(new Label
        {
            Text = "Start a conversation — type your first message below.",
            TextColor = Colors.Gray, VerticalOptions = LayoutOptions.Start,
        }, 0, 1);
        grid.Add(new VerticalStackLayout { Spacing = 8, Children = { status, composer, send } }, 0, 2);
        Content = grid;
    }
}
