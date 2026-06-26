using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging;

/// <summary>
/// A unit of work posted to a hub to be run on its single-threaded action block.
/// Carries the operation to execute plus a callback to invoke if it faults.
/// Both members are marked <c>JsonIgnore</c> because delegates are not
/// serializable — this request is dispatched in-process only.
/// </summary>
/// <param name="Action">The work to run, receiving a cancellation token; returns a task that completes when the work is done.</param>
/// <param name="ExceptionCallback">Invoked with the exception when <paramref name="Action"/> faults, giving the poster a chance to handle the failure.</param>
[PreventLogging]
public record ExecutionRequest([property:JsonIgnore]Func<CancellationToken, Task> Action, [property: JsonIgnore] Func<Exception, Task> ExceptionCallback);
