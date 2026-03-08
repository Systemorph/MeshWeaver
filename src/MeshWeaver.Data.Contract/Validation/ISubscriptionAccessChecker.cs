namespace MeshWeaver.Data.Validation;

/// <summary>
/// Checks whether the current user has read access for hub subscriptions.
/// Implemented by security services (e.g., RLS) and registered in DI.
/// Used by HandleSubscribeRequest to deny access before creating the subscription.
/// </summary>
public interface ISubscriptionAccessChecker
{
    Task<(bool Allowed, string? ErrorMessage)> CheckReadAccessAsync(string hubPath, CancellationToken ct);
}
