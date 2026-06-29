using System.Reactive.Disposables;
using Memex.Client.Prefs;
using Microsoft.Maui.ApplicationModel;

namespace Memex.Client.Pages;

/// <summary>
/// Native settings page for viewing + changing the UI zoom level. Picks a preset zoom (80% … 200%)
/// and a scope (Device = this device only, User = synced across the signed-in user's devices), then
/// saves through <see cref="IPreferencesService"/>. The live resolved zoom is shown at the top, and
/// the page applies the resolved zoom to ITSELF (<see cref="IPreferencesService.ApplyTo"/>) so a
/// change is visible immediately.
/// </summary>
public sealed class SettingsPage : ContentPage
{
    private static readonly (string Label, double Value)[] ZoomPresets =
    [
        ("80%", 0.8),
        ("100%", 1.0),
        ("125%", 1.25),
        ("150%", 1.5),
        ("200%", 2.0),
    ];

    // Scope choices exposed on this page. Device is local-only; User syncs through the mesh.
    private static readonly (string Label, PreferenceScope Scope)[] Scopes =
    [
        ("This device only", PreferenceScope.Device),
        ("My account (synced)", PreferenceScope.User),
    ];

    private readonly IPreferencesService _prefs;

    private readonly Label _current = new() { FontSize = 22, FontAttributes = FontAttributes.Bold };
    private readonly Picker _zoom = new() { Title = "Zoom level" };
    private readonly Picker _scope = new() { Title = "Apply to" };
    private readonly Button _save = new() { Text = "Save" };
    private readonly Label _status = new() { IsVisible = false, TextColor = Colors.Gray };

    private readonly CompositeDisposable _subs = new();

    public SettingsPage(IPreferencesService prefs)
    {
        _prefs = prefs;
        Title = "Settings";

        foreach (var (label, _) in ZoomPresets) _zoom.Items.Add(label);
        foreach (var (label, _) in Scopes) _scope.Items.Add(label);
        _zoom.SelectedIndex = 1;  // 100%
        _scope.SelectedIndex = 0; // this device

        _save.Clicked += OnSave;

        Content = new ScrollView
        {
            Padding = 28,
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Children =
                {
                    new Label { Text = "Display", FontSize = 28, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "Current zoom", TextColor = Colors.Gray },
                    _current,
                    new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3A3A3C"), Margin = new Thickness(0, 6) },
                    new Label { Text = "Zoom level", FontAttributes = FontAttributes.Bold },
                    _zoom,
                    new Label { Text = "Apply to", FontAttributes = FontAttributes.Bold },
                    _scope,
                    new Label
                    {
                        Text = "“This device only” is stored locally and never leaves this device. "
                             + "“My account” syncs the setting to every device you sign in on.",
                        TextColor = Colors.Gray,
                        FontSize = 12,
                    },
                    _save,
                    _status,
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Live resolved zoom → header label.
        _subs.Add(_prefs.Resolved.Subscribe(r => MainThread.BeginInvokeOnMainThread(() =>
            _current.Text = $"{r.ZoomLevel * 100:0}%")));

        // Apply the resolved zoom to this page so the change is visible immediately.
        _subs.Add(_prefs.ApplyTo(this));
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _subs.Clear();
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var zoom = ZoomPresets[Math.Clamp(_zoom.SelectedIndex, 0, ZoomPresets.Length - 1)].Value;
        var scope = Scopes[Math.Clamp(_scope.SelectedIndex, 0, Scopes.Length - 1)].Scope;

        _prefs.SetZoomLevel(scope, zoom);

        _status.IsVisible = true;
        _status.Text = scope == PreferenceScope.Device
            ? $"Saved {zoom * 100:0}% for this device."
            : $"Saved {zoom * 100:0}% to your account (syncing…).";
    }
}
