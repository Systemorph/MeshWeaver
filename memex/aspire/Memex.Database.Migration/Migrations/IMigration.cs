namespace Memex.Database.Migration.Migrations;

/// <summary>
/// A single versioned data-repair migration. Schema changes belong in
/// <c>PostgreSqlSchemaInitializer</c> (idempotent, always runs); this interface
/// is reserved for one-shot fixes to data written incorrectly by prior code versions.
/// </summary>
public interface IMigration
{
    /// <summary>Monotonically increasing version. Gaps are allowed (e.g., v12 was retired).</summary>
    int Version { get; }

    /// <summary>One-line description used for log lines.</summary>
    string Description { get; }

    Task RunAsync(MigrationContext ctx);
}
