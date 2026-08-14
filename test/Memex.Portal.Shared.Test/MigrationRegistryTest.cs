// DbVersion is compiled into BOTH assemblies on purpose (Memex.Portal.Distributed links the same
// source so the portal's gate and the runner cannot disagree), so the name is ambiguous here — the
// alias picks the runner's copy, which is the one the registry is checked against.
extern alias migration;
using Xunit;
using MigrationRegistry = migration::MigrationRegistry;
using DbVersion = migration::Memex.Database.Migration.DbVersion;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Runs the migration runner's own start-up guard inside <c>dotnet test</c>.
///
/// <para>🚨 Why this exists: <c>MigrationRegistry.VerifyComplete()</c> is called from
/// <c>Program.Main</c> — i.e. only ever INSIDE the migration container, at deploy time. So a drift
/// it is designed to catch cannot fail a PR: it fails the published image. On 2026-08-14 exactly
/// that happened. V53 was registered without bumping <c>DbVersion.Latest</c>, `main` stayed green,
/// CD published <c>memex-migration:main</c>, and every disposable-mesh e2e in every repo then died
/// at boot with <c>service "migration" didn't complete successfully: exit 139</c> — a segfault-
/// shaped symptom four steps from the actual message, which was:</para>
/// <code>
/// DbVersion.Latest (52) does not match the highest registered migration (V53).
/// </code>
/// <para>The registry's own comment already claimed a "RegisteredMigrationsTest" pinned this. No
/// such test existed. This is it.</para>
/// </summary>
public class MigrationRegistryTest
{
    [Fact]
    public void RegistryIsComplete_AndDbVersionMatchesIt()
        // Covers BOTH rules the runner enforces at start-up: every V##_ migration class in the
        // assembly is registered (the V42 incident — present, never registered, silently skipped),
        // and DbVersion.Latest equals the highest registered version (the V53 incident above).
        // Calling the guard itself, rather than restating it, keeps the test honest when the guard
        // grows a third rule.
        => MigrationRegistry.VerifyComplete();

    [Fact]
    public void DbVersionLatest_IsTheHighestRegisteredMigration()
    {
        // Stated separately so the failure NAMES the two numbers — the runner's exception does, and
        // a bare VerifyComplete() failure in a test report would not.
        var highest = MigrationRegistry.All.Max(m => m.Version);
        DbVersion.Latest.Should().Be(highest,
            "the portal's DbVersionGate is compiled from DbVersion.Latest (linked source), so a " +
            "registry that runs ahead of the constant silently loosens the startup gate — and the " +
            "migration run refuses to start at all");
    }
}
