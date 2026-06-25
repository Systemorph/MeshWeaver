using System.Reactive.Linq;
using Memex.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

namespace Memex.Client.Pages;

/// <summary>
/// First-launch onboarding page: the user fills in their full name + a short bio, then "Get started"
/// runs the framework onboarding (creates their User node + user partition + makes them global admin of
/// this instance) and takes them home. Shown as the window root until onboarded; a returning launch
/// detects the existing User node and goes straight to <see cref="PortalShellPage"/>.
/// </summary>
public sealed class OnboardingPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly DeviceOnboarding _onboarding;

    private readonly Entry _fullName = new() { Text = DeviceOnboarding.FullNameGuess() };
    private readonly Entry _role = new() { Placeholder = "Role (optional) — e.g. Developer" };
    private readonly Editor _bio = new()
    {
        Placeholder = "Write a paragraph about who you are…",
        HeightRequest = 140,
        AutoSize = EditorAutoSizeOption.TextChanges,
    };
    private readonly Button _start = new() { Text = "Get started" };
    private readonly Label _status = new() { IsVisible = false, TextColor = Colors.Gray };

    public OnboardingPage(IServiceProvider services, DeviceOnboarding onboarding)
    {
        _services = services;
        _onboarding = onboarding;
        Title = "Welcome";
        _start.Clicked += OnStart;

        Content = new ScrollView
        {
            Padding = 28,
            Content = new VerticalStackLayout
            {
                Spacing = 14,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label { Text = "👋 Welcome to Memex", FontSize = 28, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "Set up your local profile — you'll be the admin of this device's mesh.", TextColor = Colors.Gray },
                    new Label { Text = "Your name", FontAttributes = FontAttributes.Bold },
                    _fullName,
                    new Label { Text = "About you", FontAttributes = FontAttributes.Bold },
                    _bio,
                    _role,
                    _start,
                    _status,
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Already onboarded (returning launch) → skip straight to home.
        _onboarding.IsOnboarded().Subscribe(done => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (done) GoHome();
        }));
    }

    private void OnStart(object? sender, EventArgs e)
    {
        _start.IsEnabled = false;
        _status.IsVisible = true;
        _status.Text = "Setting up your mesh…";
        _onboarding.Onboard(_fullName.Text ?? "", _bio.Text, _role.Text).Subscribe(
            _ => MainThread.BeginInvokeOnMainThread(GoHome),
            ex => MainThread.BeginInvokeOnMainThread(() =>
            {
                _status.Text = $"Setup failed: {ex.Message}";
                _start.IsEnabled = true;
            }));
    }

    private void GoHome()
    {
        if (Window is not null)
            Window.Page = _services.GetRequiredService<PortalShellPage>();
    }
}
