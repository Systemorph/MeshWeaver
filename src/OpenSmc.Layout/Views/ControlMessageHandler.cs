using System.Collections.Immutable;

namespace OpenSmc.Layout.Views;


public abstract record ControlMessageHandler
{
    public abstract Task Execute(object message);
}
public record ControlMessageHandler<TControl>(TControl Control) : ControlMessageHandler
{
    private ImmutableDictionary<Type, Func<TControl,object,Task>> Events { get; init; } = ImmutableDictionary<Type, Func<TControl, object, Task>>.Empty;
    public ControlMessageHandler<TControl> WithEvent<TEvent>(Func<TControl, TEvent, Task> action)
    {
        return this with { Events = Events.SetItem(typeof(TEvent), (c, e) => action(c, (TEvent)e)) };
    }

    public override Task Execute(object message)
    {
        var tEvent = message.GetType();
        if (Events.TryGetValue(tEvent, out var action))
            return action(Control, message);
        return Task.CompletedTask;
    }
}



