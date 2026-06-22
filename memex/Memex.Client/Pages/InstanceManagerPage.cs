using Memex.Client.Services;

namespace Memex.Client.Pages;

/// <summary>
/// The app's start page: a native management GUI for memex instances. The user adds an instance by
/// base URL (then authenticates via OAuth — wired separately), removes instances, and opens one —
/// which loads that portal (memex-primary) in <see cref="PortalHostPage"/>.
/// </summary>
public sealed class InstanceManagerPage : ContentPage
{
    private readonly InstanceStore _store;
    private readonly VerticalStackLayout _list;
    private readonly Entry _name = new() { Placeholder = "Name (optional)" };
    // Pre-filled with a real value (not a grey placeholder that looks filled but isn't) — the public
    // memex is the default, so "Add instance" works on first tap.
    private readonly Entry _url = new() { Text = "https://memex.meshweaver.cloud", Keyboard = Keyboard.Url };

    public InstanceManagerPage(InstanceStore store)
    {
        _store = store;
        Title = "Memex";

        _list = new VerticalStackLayout { Spacing = 8 };
        var add = new Button { Text = "Add instance" };
        add.Clicked += OnAdd;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 16,
                Children =
                {
                    new Label { Text = "Your memex instances", FontSize = 22, FontAttributes = FontAttributes.Bold },
                    _list,
                    new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.4 },
                    new Label { Text = "Add an instance", FontAttributes = FontAttributes.Bold },
                    _name,
                    _url,
                    add,
                    new Label
                    {
                        Text = "Enter the portal base URL. Open it and sign in with OAuth in the portal.",
                        FontSize = 12, TextColor = Colors.Gray
                    },
                }
            }
        };

        Refresh();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh(); // reflect token/auth changes made while inside an instance
    }

    private void OnAdd(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_url.Text)) return;
        _store.Add(_name.Text ?? "", _url.Text);
        _name.Text = _url.Text = "";
        Refresh();
    }

    private void Refresh()
    {
        _list.Clear();
        if (_store.Instances.Count == 0)
        {
            _list.Add(new Label { Text = "No instances yet — add one below.", TextColor = Colors.Gray });
            return;
        }

        foreach (var instance in _store.Instances)
        {
            var inst = instance;

            var open = new Button { Text = inst.Name, HorizontalOptions = LayoutOptions.Fill };
            open.Clicked += async (_, _) => await Navigation.PushAsync(new PortalHostPage(_store, inst));

            var del = new Button { Text = "✕", WidthRequest = 44 };
            del.Clicked += (_, _) => { _store.Remove(inst); Refresh(); };

            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
            row.Add(open, 0);
            row.Add(del, 1);

            _list.Add(row);
            _list.Add(new Label
            {
                Text = inst.IsAuthenticated ? inst.Url : $"{inst.Url}  (not signed in)",
                FontSize = 11,
                TextColor = Colors.Gray
            });
        }
    }
}
