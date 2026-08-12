using MeshWeaver.Hosting;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// The <see cref="CircuitHandler"/> that feeds <see cref="ActiveCircuitTracker"/>. Deliberately
/// separate from <c>CircuitAccessHandler</c>: that one carries per-circuit identity and is scoped to
/// the circuit, while this one only ticks a process-wide counter. Keeping them apart means the
/// shutdown signal cannot be broken by a change to identity handling, and vice versa.
/// </summary>
public sealed class CircuitDrainHandler(ActiveCircuitTracker tracker) : CircuitHandler
{
    /// <inheritdoc />
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.Opened();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.Closed();
        return Task.CompletedTask;
    }
}
