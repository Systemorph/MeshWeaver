using System;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Decides — from the connection string alone — how a Postgres connection authenticates, so that
/// every caller selects the Azure Entra-ID (managed-identity) token path by the SAME rule.
///
/// <para>The rule callers kept getting wrong was a substring match on the whole connection string:
/// <c>connectionString.Contains("database.azure.com")</c>. That is wrong twice over, and the
/// second way is a production crash:</para>
/// <list type="number">
/// <item>A substring can false-match a password, database or application name that merely contains
/// the text — it does not tell you the <em>host</em> is Azure-managed.</item>
/// <item>It ignores whether a PASSWORD is present. Aspire's <c>AddAzureNpgsqlDataSource</c> — and
/// every hand-wired <c>UsePeriodicPasswordProvider</c> / <c>UsePasswordProvider</c> — registers a
/// token password provider, and Npgsql throws
/// <c>NotSupportedException: "When registering a password provider, a password or password file
/// may not be set"</c> the instant a connection opens if the string ALSO carries a password. On
/// the portal that surfaces as <c>PostgreSqlChangeListener</c> faulting and the process aborting
/// (SIGABRT). So a fully-qualified Azure host reached with username+password MUST take the plain
/// Npgsql (password) path, never the Entra path.</item>
/// </list>
///
/// <para>The canonical implementation this mirrors is
/// <c>Memex.Database.Migration/Migrations/SchemaHelpers.BuildSchemaDataSource</c>
/// (<c>isAzure = host ends in the Azure suffix</c>; token provider wired only when
/// <c>isAzure &amp;&amp; !hasPassword</c>).</para>
/// </summary>
public static class AzurePostgres
{
    /// <summary>The Azure Database for PostgreSQL (Flexible/Single Server) host suffix.</summary>
    public const string HostSuffix = ".postgres.database.azure.com";

    /// <summary>
    /// True when the connection string targets an Azure-managed Postgres — its <c>Host</c> ends in
    /// <see cref="HostSuffix"/>. A HOST test, never a substring match on the whole string: a
    /// password or database name that happens to contain the suffix does not make a server
    /// Azure-managed. An empty or unparseable connection string is not Azure.
    /// </summary>
    public static bool IsAzureHost(string? connectionString)
        => TryParse(connectionString, out var csb)
           && csb.Host?.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// True when the connection must authenticate with an Azure Entra-ID (managed-identity) TOKEN
    /// rather than a password — i.e. it targets an Azure host AND carries no password. This is the
    /// ONLY condition under which a token password provider (<c>AddAzureNpgsqlDataSource</c> /
    /// <c>UsePeriodicPasswordProvider</c> / <c>UsePasswordProvider</c>) may be wired; wiring it
    /// when a password is present makes Npgsql throw on connect (see the class remarks).
    /// </summary>
    public static bool UsesManagedIdentityAuth(string? connectionString)
        => TryParse(connectionString, out var csb)
           && csb.Host?.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase) == true
           && string.IsNullOrEmpty(csb.Password);

    /// <summary>
    /// Parse a connection string, treating ANY failure (null/empty/whitespace, an unknown keyword,
    /// a malformed pair) as "not a connection string we can classify" → the caller falls to the
    /// plain path, where the real parse error surfaces when the data source is actually built.
    /// Npgsql's parser throws several unrelated types for malformed input
    /// (<see cref="System.ArgumentException"/>, <see cref="System.Collections.Generic.KeyNotFoundException"/>,
    /// <see cref="FormatException"/>), so this catch is deliberately broad.
    /// </summary>
    private static bool TryParse(string? connectionString, out NpgsqlConnectionStringBuilder csb)
    {
        csb = null!;
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;
        try
        {
            csb = new NpgsqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
