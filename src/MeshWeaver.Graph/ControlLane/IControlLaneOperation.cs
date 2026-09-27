using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>
/// One operation the control lane can execute on a target — registered in CODE (DI), claiming one
/// value of the open <see cref="ControlLaneOperation"/> vocabulary.
///
/// <para>🚨 Registered in code rather than as a rule NODE on purpose. The open-vocabulary rule
/// (Doc/Architecture/OpenVocabulariesAsStringConstants) resolves values through mesh nodes, which a
/// Code node can extend without a deployment. For an executor a REMOTE signed request can invoke as
/// system, that extensibility is exactly the attack surface: a node anyone with write access could
/// edit would become a remote system shell. So the VOCABULARY stays open — a module claims a new
/// value by registering its own implementation, and an unclaimed value is refused by name — while
/// the executors are compiled, reviewed and shipped in an image.</para>
/// </summary>
public interface IControlLaneOperation
{
    /// <summary>The <see cref="ControlLaneRequest.Operation"/> value this claims.</summary>
    string Operation { get; }

    /// <summary>
    /// Why the request's SHAPE is not acceptable for this operation (a missing confirmation, a
    /// target that is not one segment), or null. Pure — answered before anything is recorded.
    /// </summary>
    string? ShapeRefusal(ControlLaneRequest request);

    /// <summary>
    /// Reads what the request would act on, AS SYSTEM and writing nothing, and answers the plan
    /// with the work that executes it. Errors carry the refusal (a protected partition, a target
    /// the index could not see, nothing to delete). Cold.
    /// </summary>
    IObservable<ControlLanePreparation> Prepare(IMessageHub hub, ControlLaneRequest request);
}

/// <summary>
/// A computed plan and the work that executes exactly it: <see cref="Execute"/> emits progress
/// lines and completes when the operation is VERIFIED done; its last line is the audit line. It
/// errors on any failure, naming it. Nothing runs until <see cref="Execute"/> is subscribed.
/// </summary>
/// <param name="Plan">What would run — the approval binds its digest.</param>
/// <param name="Execute">Runs the plan, as system. Cold.</param>
public sealed record ControlLanePreparation(ControlLanePlan Plan, Func<IObservable<string>> Execute);
