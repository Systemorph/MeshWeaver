namespace MeshWeaver.Mesh;

/// <summary>
/// The clock the read budget of <c>GetMeshNodeOutcome</c> runs on, registered as a mesh service.
/// Absent — every production mesh — the budget runs on <see cref="TimeProvider.System"/>.
///
/// <para>🚨 A DEDICATED type, deliberately not a bare <see cref="TimeProvider"/> resolved from the
/// container: an unrelated <see cref="TimeProvider"/> registration (an HTTP resilience pipeline, a
/// library that registers its own clock) must never silently take over every node read's budget.
/// Registering this record is an explicit statement that the read budget runs on that clock — which
/// is what a test whose SUBJECT is the budget elapsing does, so it can fire the budget by advancing
/// a virtual clock instead of sitting it out in real time.</para>
/// </summary>
/// <param name="Clock">The clock the read budget runs on.</param>
public sealed record ReadBudgetClock(TimeProvider Clock);
