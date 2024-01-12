using System;
using System.Collections.Immutable;

namespace OpenSmc.Application;

public record SetAreaOptions(string Area)
{
    public object AreaViewOptions { get; init; }

    internal ImmutableList<Action<AreaChangedEvent>> Callbacks { get; init; } = ImmutableList<Action<AreaChangedEvent>>.Empty;

    public SetAreaOptions WithCallback(Action<AreaChangedEvent> callback) => this with { Callbacks = Callbacks.Add(callback) };

    public SetAreaOptions WithArea(string area) => this with { Area = area };
}