using System.Reactive.Linq;
using Memex.Client.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;

namespace Memex.Client.Pages;

/// <summary>
/// A native text chat composer that talks to on-device Apple Intelligence (<see cref="IOnDeviceChat"/>) —
/// the same on-GPU model the Voice page uses, but typed. A scrolling transcript of bubbles + a native
/// <see cref="Editor"/> composer + Send. Fully offline; no mesh agent required.
///
/// <para>This is the on-device companion to the (still-unfinished, agent-backed) mesh-thread
/// <c>ThreadChatControl</c>; the native <see cref="Editor"/> composer here is the same input the mesh-thread
/// version will reuse once that control + its <c>hub.SubmitMessage</c> path are wired (Monaco stays an
/// optional later WebView).</para>
/// </summary>
public sealed class ChatView : ContentView
{
    private readonly IOnDeviceChat _ai;
    private readonly VerticalStackLayout _messages = new() { Spacing = 8 };
    private readonly ScrollView _scroll;
    // Monaco editor (in a native WebView) — the same rich editor the web composer uses, injected as JS.
    private readonly MonacoEditorView _input = new(language: "markdown", placeholder: "Message…")
    {
        HeightRequest = 120,
    };
    private readonly Button _send = new() { Text = "Send" };
    private readonly Label _status = new() { FontSize = 11, TextColor = Colors.Gray };

    public ChatView(IOnDeviceChat ai)
    {
        _ai = ai;
        _status.Text = Available ? "On-device · Apple Intelligence" : "On-device AI unavailable on this device";
        _send.Clicked += OnSend;
        _scroll = new ScrollView { Content = _messages, VerticalOptions = LayoutOptions.Fill };

        // Composer: the Monaco editor + a Send button. Native pickers (agent/model) layer in next.
        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { _send } };
        var composer = new VerticalStackLayout { Spacing = 6, Children = { _input, buttons } };

        var root = new Grid
        {
            Padding = 12,
            RowSpacing = 8,
            RowDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
        };
        root.Add(_scroll, 0, 0);
        root.Add(_status, 0, 1);
        root.Add(composer, 0, 2);
        Content = root;
    }

    private bool Available => _ai.Availability == OnDeviceChatAvailability.Available;

    private async void OnSend(object? sender, EventArgs e)
    {
        var text = (await _input.GetTextAsync()).Trim();
        if (string.IsNullOrEmpty(text)) return;

        AddBubble(text, user: true);
        _input.SetText("");

        if (!Available)
        {
            AddBubble("On-device AI is unavailable on this device.", user: false);
            return;
        }

        _status.Text = "Thinking on-device…";
        _send.IsEnabled = false;
        _ai.Respond(text).Subscribe(reply => MainThread.BeginInvokeOnMainThread(() =>
        {
            AddBubble(reply, user: false);
            _status.Text = "On-device · Apple Intelligence";
            _send.IsEnabled = true;
        }), _ => MainThread.BeginInvokeOnMainThread(() =>
        {
            _status.Text = "On-device · Apple Intelligence";
            _send.IsEnabled = true;
        }));
    }

    private void AddBubble(string text, bool user)
    {
        _messages.Children.Add(new Border
        {
            Padding = new Thickness(10, 6),
            Margin = new Thickness(user ? 40 : 0, 0, user ? 0 : 40, 0),
            HorizontalOptions = user ? LayoutOptions.End : LayoutOptions.Start,
            BackgroundColor = user ? Colors.RoyalBlue : Color.FromArgb("#3A3A3C"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = new Label { Text = text, TextColor = Colors.White },
        });
        // Scroll to the newest bubble.
        MainThread.BeginInvokeOnMainThread(() => _scroll.ScrollToAsync(_messages, ScrollToPosition.End, animated: true));
    }
}
