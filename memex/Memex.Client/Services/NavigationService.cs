using System.Collections.Immutable;
using System.Reactive.Subjects;

namespace Memex.Client.Services;

/// <summary>
/// Where the shell currently is: a node <see cref="NodePath"/> + the <see cref="Area"/> being viewed
/// (a <c>null</c> path = a chrome destination such as search/instances that has no node menu), plus a
/// <see cref="Build"/> factory the shell calls to materialise the content view. <see cref="NodePath"/> +
/// <see cref="Area"/> are the inputs the shell feeds to <c>hub.GetMenu</c> to load the platform menu —
/// exactly the way the Blazor portal derives its top menu from the area currently routed.
/// </summary>
public sealed record NavLocation(string Title, string? NodePath, string Area, Func<View> Build);

/// <summary>
/// Browser-style navigation state for the portal shell — the single source of truth for "where we are".
/// Holds a back/forward history whose CURRENT entry drives both the content frame and the top-bar
/// platform menu. Exposed reactively (<see cref="Current"/>) so the shell re-renders content + reloads the
/// menu whenever the location changes. This is the explicit "navigation service" the portal routes through;
/// the menu is always loaded from <see cref="CurrentLocation"/>.
/// </summary>
public sealed class NavigationService
{
    private ImmutableList<NavLocation> _history = ImmutableList<NavLocation>.Empty;
    private int _index = -1;
    private readonly BehaviorSubject<NavLocation?> _current = new(null);

    /// <summary>The current location ("where we are"); emits <c>null</c> before the first navigation.</summary>
    public IObservable<NavLocation?> Current => _current;

    public NavLocation? CurrentLocation => _index >= 0 && _index < _history.Count ? _history[_index] : null;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index < _history.Count - 1;

    /// <summary>Navigate to a new location, truncating any forward history (browser semantics).</summary>
    public void Navigate(NavLocation location)
    {
        if (_index < _history.Count - 1)
            _history = _history.RemoveRange(_index + 1, _history.Count - 1 - _index);
        _history = _history.Add(location);
        _index = _history.Count - 1;
        _current.OnNext(location);
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        _index--;
        _current.OnNext(_history[_index]);
    }

    public void GoForward()
    {
        if (!CanGoForward) return;
        _index++;
        _current.OnNext(_history[_index]);
    }
}
