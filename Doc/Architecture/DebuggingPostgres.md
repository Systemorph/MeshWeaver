---
Name: Debugging Postgres in Prod / Test
Category: Documentation
Description: How to connect to the Aspire-deployed Azure Postgres Flexible Server using Azure AD auth, run ad-hoc queries, and inspect migration state.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M20 5v14c0 1.66-3.58 3-8 3s-8-1.34-8-3V5"/><path d="M4 12c0 1.66 3.58 3 8 3s8-1.34 8-3"/></svg>
---

# Connecting to Aspire's Azure Postgres

The deployed clusters (`prod`, `test`) provision **Azure Postgres Flexible Server with password auth disabled** — only Azure AD entra-id auth is accepted. Password from `dotnet user-secrets` is for legacy/break-glass and **does not work**.

## Quick connect

| Mode | FQDN |
|---|---|
| prod | `memexpostgres-d272wxvys4nvo.postgres.database.azure.com` |
| test | look up: `az postgres flexible-server list -g test-memex --query "[].fullyQualifiedDomainName" -o tsv` |

```bash
# Verify which AAD identity you're signed in as — your user must be granted
# Postgres AAD admin (or be a member of an AAD group that is).
az account show --query "user.name" -o tsv

# 1-shot token good for ~1 hour
PGPASSWORD=$(az account get-access-token \
  --resource-type oss-rdbms --query accessToken -o tsv)

psql "host=memexpostgres-d272wxvys4nvo.postgres.database.azure.com \
      port=5432 dbname=memex user=$(az account show --query user.name -o tsv) \
      sslmode=require"
```

The `oss-rdbms` token resource maps to `https://ossrdbms-aad.database.windows.net/.default`. SSL is mandatory.

If `psql` is not installed: `winget install PostgreSQL.PostgreSQL` (Windows) — picks up `psql.exe` on `PATH`. Or use the C# script below.

## C# alternative (no psql install needed)

Drop a script under `tools/`:

```csharp
#r "nuget: Npgsql, 9.0.2"
#r "nuget: Azure.Identity, 1.13.1"
using Azure.Core;
using Azure.Identity;
using Npgsql;

const string Host = "memexpostgres-d272wxvys4nvo.postgres.database.azure.com";
const string Db   = "memex";
const string User = "rbuergi@systemorph.com"; // your AAD UPN

var token = await new DefaultAzureCredential().GetTokenAsync(
    new TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }));
await using var conn = new NpgsqlConnection(
    $"Host={Host};Database={Db};Username={User};Password={token.Token};SSL Mode=Require");
await conn.OpenAsync();

await using var cmd = new NpgsqlCommand("SELECT current_user, version()", conn);
await using var rdr = await cmd.ExecuteReaderAsync();
while (await rdr.ReadAsync())
    Console.WriteLine($"{rdr[0]}  {rdr[1]}");
```

Run with `dotnet script tools/your-query.csx` (one-time `dotnet tool install -g dotnet-script` if missing).

There's a worked example at `tools/check-prod-db.csx` — uses the same pattern to run a battery of diagnostic queries (migration version, per-user schemas, access assignments, thread distribution).

## Cheat sheet for migration / partition state

```sql
-- 1. What migration version did the runner reach?
SELECT id, content
  FROM admin.mesh_nodes
 WHERE id = 'db_version';

-- 2. Per-user / per-org content schemas (post-V10 layout)
SELECT schema_name FROM information_schema.schemata s
 WHERE EXISTS (SELECT 1 FROM information_schema.tables t
               WHERE t.table_schema = s.schema_name AND t.table_name='mesh_nodes')
   AND s.schema_name NOT IN ('public','admin','information_schema','pg_catalog','pg_toast','user')
   AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
 ORDER BY schema_name;

-- 3. Where do AccessAssignments live for a given user?
SELECT 'user'      AS schema, namespace, content
  FROM "user".access  WHERE content->>'accessObject' = 'rbuergi'
UNION ALL
SELECT 'partnerre' AS schema, namespace, content
  FROM partnerre.access WHERE content->>'accessObject' = 'rbuergi';

-- 4. Cross-schema search for a node by id (use when "where does X live?" is the question)
DO $$
DECLARE r RECORD;
BEGIN
    FOR r IN SELECT schema_name FROM information_schema.schemata s
             WHERE EXISTS (SELECT 1 FROM information_schema.tables t
                           WHERE t.table_schema = s.schema_name AND t.table_name='mesh_nodes')
               AND s.schema_name NOT IN ('information_schema','pg_catalog','pg_toast','public')
    LOOP
        EXECUTE format(
          'SELECT %L AS schema, id, namespace, node_type FROM %I.mesh_nodes WHERE id = ''loss-model''',
          r.schema_name, r.schema_name);
    END LOOP;
END $$;
```

## Reading migration logs

The migration runs as an Aspire `db-migration` resource that completes **before** the portal starts. Logs are in Container Apps:

```bash
az containerapp logs show -n db-migration -g prod-memex --tail 200
# follow live:
az containerapp logs show -n db-migration -g prod-memex --follow
```

If migration crashed mid-run, you'll see the `Unhandled exception` at the bottom and the partial schema state in the DB. The `db_version` row is only written **after** all migrations complete cleanly — so a missing `db_version` plus a non-empty schema set means the runner crashed mid-flight.

## Common failure modes

- **`28000: no pg_hba.conf entry for host … user "app"`** — the migration code built an `NpgsqlDataSource` from the raw connection string instead of going through the Aspire-configured Azure-AD password provider. Every per-schema datasource (e.g. `SchemaHelpers.BuildSchemaDataSource`) needs the same AAD token-acquisition hook the main runner uses. Fix the helper to wire `dsb.UsePeriodicPasswordProvider(...)` instead of `dsb.Build()` directly.
- **Migration aborts mid-run, `db_version` missing** — see above. The runner only persists `db_version` after the loop completes; a single failed `Vxx` leaves the version unchanged. After fixing the underlying issue, the runner re-runs every migration `> 0` (i.e., everything) on next deploy.
- **AAD token expired during long migration** — `az account get-access-token` issues a token with ~1h lifetime. Migrations that exceed that need `UsePeriodicPasswordProvider` (refreshes automatically) rather than a one-shot password. The Aspire `AddAzureNpgsqlDataSource` already does this for the main connection.
- **Wrong AAD identity** — token is for whoever `az login` was last run as. If your user isn't a Postgres AAD admin, `28000` again. Add via portal: *Azure Database for PostgreSQL → Authentication → Add Microsoft Entra admin*.

## Where the prod DB lives

| Resource | Value |
|---|---|
| Resource Group | `prod-memex` |
| Server | `memexpostgres-d272wxvys4nvo.postgres.database.azure.com` |
| Database | `memex` |
| Auth | Azure AD only (password disabled) |
| Tenant | `3a01d7ac-3330-444d-942d-975eb491b5d6` |
| Logs | Loki (via Promtail scraping pod stdout); metrics/traces via OTLP → Prometheus/Grafana |

For test cluster, swap `prod-memex` → `test-memex` and discover the FQDN with the `az postgres flexible-server list` command above.
