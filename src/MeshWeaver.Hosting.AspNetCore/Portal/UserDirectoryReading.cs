namespace MeshWeaver.Hosting.AspNetCore.Portal;

/// <summary>
/// One consistent reading of the portal's user directory (<see cref="UserIdentityCache"/>).
/// The readiness gate that classifies it (<c>UserDirectoryReadiness</c>) is host code and lives in
/// MeshWeaver.Plugins (<c>MeshWeaver.Hosting.AspNetCore.Portal</c>); this record stays here because
/// <see cref="UserIdentityCache.Reading"/> returns it and published module bytes bind the cache.
/// </summary>
/// <param name="Hydrated">A snapshot is applied right now, so a miss is a definitive "no such user".</param>
/// <param name="EverHydrated">A snapshot has been applied at least once in this process.</param>
/// <param name="Failure">Why the directory read terminated, or <c>null</c> while it is live.</param>
/// <param name="IndexedUsers">How many users the index holds by email.</param>
public readonly record struct UserDirectoryReading(
    bool Hydrated, bool EverHydrated, string? Failure, int IndexedUsers);
