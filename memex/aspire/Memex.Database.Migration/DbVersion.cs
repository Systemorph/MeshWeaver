namespace Memex.Database.Migration;

/// <summary>
/// The one shared "latest DB version" constant. Compiled into BOTH this migration project and
/// <c>Memex.Portal.Distributed</c> (linked source in its csproj), so the portal's
/// <c>DbVersionGate</c> and the migration runner cannot disagree — the gate sat at 32 while
/// migrations ran to V52 because the two numbers lived apart and the "bump in lock-step"
/// comment was the only coupling.
///
/// <para><c>MigrationRegistry.VerifyComplete()</c> pins this to the highest registered
/// <c>V##_*</c> migration at migration startup: registering a new migration without bumping
/// this constant crashes the migration run loudly instead of letting the portal gate drift.</para>
/// </summary>
public static class DbVersion
{
    public const int Latest = 52;
}
