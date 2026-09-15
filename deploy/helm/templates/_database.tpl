{{- /*
🚨 THE DATABASE CONNECTIONS, AS ONE DEFINITION — and the hosts a start-up gate must actually wait
   for (MeshWeaver#4173, the guard half of #3780).

   THE DEFECT THIS CLOSES. The `wait-for-postgres` init container existed to make one thing
   deterministic: "Postgres and the portal start concurrently on a fresh install; the portal's hub
   init FAILS (and the host aborts) if Postgres isn't yet accepting connections." It probed ONE
   host, and that host came from a DIFFERENT input than the connections the process opens —
   `config.<half>.MEMEX_HOST`, a CONFIG value, while the boot opens `ConnectionStrings__memex` and
   `ConnectionStrings__orleans`, which are SECRETS rendered from the Key Vault values half that no
   repository holds. Nothing rendered, asserted or logged that those hosts were the same one.

   So an explicit `ConnectionStrings__orleans` naming another host — a dedicated `orleans` server,
   or the same server under another name — was gated by NOTHING. The pod passes `Init:1/1`, the
   silo reaches lifecycle stage `RuntimeGrainServices`, and `MembershipTableManager` fails on a
   host that never resolved:

       'MembershipTableManager' failed to start due to errors at stage 'RuntimeGrainServices (8000)'.
       Npgsql.NpgsqlException: Name or service not known
        ---> System.Net.Sockets.SocketException: Name or service not known
          at System.Net.Dns.GetHostEntryOrAddressesCore(...)

   — and the container exits, because the portal fails closed. 🚨 The reading that costs the time is
   the init container's PASS: it says nothing about the connection that failed, so the failure reads
   as a transient resolver blip rather than as a gate that was never covering that host. That is the
   same shape as a CI job that skips wearing a passing tick.

   WHAT IS HERE. Every database connection string the chart renders, and the endpoints derived FROM
   those strings, defined once and consumed by both the Secret that carries them and the init
   container that waits for them. One definition, so the gate cannot drift from the connection —
   the property `memex-migration/secrets.yaml` already CLAIMED ("both are derived by the same rule
   from the same input rather than typed twice") while the rule was in fact written out twice.

   🚨 NO EMPTY PROBE LIST. `memex.dbProbeTargets` calls `fail` rather than rendering nothing: an
   init container running `until nc -z ; do …` would either spin forever or pass having probed
   nothing, and "the gate could not determine what to wait for" must never render as "the gate
   passed". Same rule as AGENTS.md → "A gate NEVER tests its own inputs".

   🚨 THE KEY VAULT CASE — the mesh string is not always in the values. Every record-driven instance
   since MeshWeaver.Plugins#1721 carries `ConnectionStrings__memex` in Key Vault (`<prefix>db-
   connection`), mounted by a CSI SecretProviderClass whose synced Secret OUTRANKS the chart's own
   Secret in `envFrom`. Its values carry NO connection string, so "derive the host from the values'
   connection string" derived it from the chart's in-cluster DEFAULT, `Host=memex-postgres-service`,
   a Service a `postgres.enabled: false` release does not render. Measured on pearl, 2026-09-15
   10:15–10:30Z (chart 0a45bccfc, the first provision after #4173): the migration Job and the portal
   both looped `waiting for postgres at memex-postgres-service:5432` / `nc: bad address
   'memex-postgres-service'` forever — while the record HAD rendered the right server into
   `config.<half>.MEMEX_HOST` (`memexaks-pg.postgres.database.azure.com`). `build`, provisioned on the
   pre-#4173 chart that probed MEMEX_HOST, came up. So `memex.meshProbeGroup` below is the ONE place
   the mesh endpoint is chosen, per half:
     * `postgres.enabled` → the in-cluster Service (the only case that may ever name it);
     * the values carry `secrets.<half>.ConnectionStrings__memex` → the host that string names
       (#4173, unchanged);
     * otherwise — an EXTERNAL database whose string arrives from outside the values (Key Vault CSI,
       or a hand-made class) → `config.<half>.MEMEX_HOST:MEMEX_PORT`, the record-rendered address of
       the same server;
     * and when even that is blank or still the in-cluster default → the render FAILS, naming both
       inputs (the #3780 rule: refuse, never invent a host).

   Every template here takes `(dict "root" $ "half" "memex_portal" | "memex_migration")`.
*/ -}}

{{- /* The MESH database connection the half opens — the value rendered into its Secret. */ -}}
{{- define "memex.meshConnectionString" -}}
{{- $secrets := index .root.Values.secrets .half -}}
{{- $secrets.ConnectionStrings__memex | default (printf "Host=memex-postgres-service;Port=5432;Username=postgres;Password=%s;Database=memex" $secrets.memex_postgres_password) -}}
{{- end -}}

{{- /* Is this deployment on AdoNet clustering? Read from the PORTAL's config for BOTH halves — the
       migration provisions what the silo will use, so a migration that asked its own half could
       provision for a cluster shape the portal does not run. */ -}}
{{- define "memex.adoNetClustering" -}}
{{- if eq ((.root.Values.config.memex_portal).Deployment__Orleans__Clustering | default "Localhost") "AdoNet" -}}true{{- end -}}
{{- end -}}

{{- /* The ORLEANS connection the half opens. Two supported shapes, in order:
         * explicit — whatever `ConnectionStrings__orleans` says (a dedicated `orleans` database on
           the same server, or another server entirely); it always wins;
         * derived  — the mesh connection plus `Search Path=orleans`: the SAME database as the
           mesh/graph data, isolated in its own schema. The default for an external mesh database,
           so a multi-pod deployment is never left on Localhost membership.
       Only meaningful under AdoNet; the caller asks `memex.adoNetClustering` first.

       🚨 THE THIRD SHAPE IS A REFUSAL, NOT A DEFAULT (MeshWeaver#3780). With neither string set the
       only value left is the chart's in-cluster host, `memex-postgres-service` — correct where
       `postgres.enabled` renders that Service (self-host, compose, local k3s), and a dead name
       everywhere else. Cluster membership pointed at a Service the release does not render fails
       the silo at start ('MembershipTableManager' … Name or service not known) on EVERY new pod,
       and nothing in the chart said which input was missing. Measured on the control instance on
       2026-09-09 (revision 44) and 2026-09-14 (revision 55): an upgrade that was fed the record's
       render without the Key Vault values half re-rendered memex-portal-secrets from exactly this
       default. So on an external database the rule fails the RENDER, naming the input, before
       anything is applied. The mesh string is deliberately NOT gated the same way: a record-driven
       instance legitimately supplies ConnectionStrings__memex through a Key Vault CSI class that
       outranks the chart's Secret in envFrom, so its chart-side copy is a shadowed placeholder —
       while ConnectionStrings__orleans has no such source anywhere in the fleet. */ -}}
{{- define "memex.orleansConnectionString" -}}
{{- $secrets := index .root.Values.secrets .half -}}
{{- $orleans := $secrets.ConnectionStrings__orleans -}}
{{- if and (not $orleans) $secrets.ConnectionStrings__memex -}}
{{- $orleans = printf "%s;Search Path=orleans" (trimSuffix ";" $secrets.ConnectionStrings__memex) -}}
{{- end -}}
{{- if and (not $orleans) (not .root.Values.postgres.enabled) -}}
{{- fail (printf "memex.orleansConnectionString: '%s' runs AdoNet clustering on an EXTERNAL database (postgres.enabled is false) but names no database for cluster membership: neither secrets.%s.ConnectionStrings__orleans nor secrets.%s.ConnectionStrings__memex is set. The only value left would be the chart's in-cluster default Host=memex-postgres-service, a Service this release does not render — every new pod would then fail at silo start ('MembershipTableManager' failed to start … Name or service not known; MeshWeaver#3780). Supply the connection string in values: on a record-driven deploy that is the Key Vault values half (helm-values-<release>, layered by hosting-deploy --vault when the record declares vaultValuesKeys); on the helm-release lane it is layered as vault-values.yaml. Refusing to render." .half .half .half) -}}
{{- end -}}
{{- $orleans | default (printf "Host=memex-postgres-service;Port=5432;Username=postgres;Password=%s;Database=orleans" $secrets.memex_postgres_password) -}}
{{- end -}}

{{- /* One ADO.NET connection string as a HOST GROUP — `h1,h2:port` — or the empty string when it
       names no host. Npgsql accepts `Host=` and `Server=`, case-insensitively, defaults the port to
       5432, and accepts a COMMA-SEPARATED failover list.

       🚨 Every member of that list is kept, and the waiter below treats the group as "any ONE of
       these answers". Keeping only the first would let the gate pass having waited for `a` while
       the process connects to `b` — the same "the gate passed says nothing about the connection
       that failed" defect one level down. Requiring ALL of them would be the opposite error: the
       point of a failover list is that one member suffices, so demanding a standby be up would
       block a perfectly valid deployment. */ -}}
{{- define "memex.dbHostGroup" -}}
{{- $cs := . -}}
{{- $hostMatch := regexFind "(?i)(^|;)[ \t]*(host|server)[ \t]*=[ \t]*[^;]+" $cs -}}
{{- if $hostMatch -}}
{{- $hosts := list -}}
{{- range splitList "," (last (splitList "=" $hostMatch)) -}}
{{- if trim . -}}{{- $hosts = append $hosts (trim .) -}}{{- end -}}
{{- end -}}
{{- if $hosts -}}
{{- $port := "5432" -}}
{{- $portMatch := regexFind "(?i)(^|;)[ \t]*port[ \t]*=[ \t]*[0-9]+" $cs -}}
{{- if $portMatch -}}{{- $port = trim (last (splitList "=" $portMatch)) -}}{{- end -}}
{{- printf "%s:%s" (join "," $hosts) $port -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- /* The MESH endpoint the half's gate waits for, as a host group — see the Key Vault case in the
       header. The in-cluster Service is named ONLY when `postgres.enabled` renders it: on an
       external database a probe of `memex-postgres-service` can never succeed and never fail, it
       just spins (pearl, 2026-09-15). */ -}}
{{- define "memex.meshProbeGroup" -}}
{{- $secrets := index .root.Values.secrets .half | default dict -}}
{{- if or .root.Values.postgres.enabled $secrets.ConnectionStrings__memex -}}
{{- include "memex.dbHostGroup" (include "memex.meshConnectionString" .) -}}
{{- else -}}
{{- $config := index .root.Values.config .half | default dict -}}
{{- $host := trim (toString ($config.MEMEX_HOST | default "")) -}}
{{- if or (not $host) (eq $host "memex-postgres-service") -}}
{{- fail (printf "memex.dbProbeTargets: '%s' runs on an EXTERNAL database (postgres.enabled is false) but names no external database host: secrets.%s.ConnectionStrings__memex is not in values (the Key Vault case — the string arrives through a CSI SecretProviderClass) and config.%s.MEMEX_HOST is %s. The only host left would be the chart's in-cluster default memex-postgres-service, a Service this release does not render, and wait-for-postgres would spin on it forever (pearl, 2026-09-15). Set config.%s.MEMEX_HOST (the record renders it from databaseServer/databaseHost) or supply the connection string in values. The same refusal as MeshWeaver#3780: never invent a database host." .half .half .half (ternary "blank" "still the in-cluster default memex-postgres-service" (not $host)) .half) -}}
{{- end -}}
{{- printf "%s:%s" $host (trim (toString ($config.MEMEX_PORT | default "5432"))) -}}
{{- end -}}
{{- end -}}

{{- /* Every DISTINCT host GROUP the half's process will open, space-separated — the mesh database
       always, and the orleans one whenever AdoNet clustering is configured. Deduplicated, because
       the derived orleans string names the same server as the mesh one and probing it twice says
       nothing extra.

       🚨 Fails the render when no host could be derived. See the header. */ -}}
{{- define "memex.dbProbeTargets" -}}
{{- $targets := list -}}
{{- $mesh := include "memex.meshProbeGroup" . -}}
{{- if $mesh -}}{{- $targets = append $targets $mesh -}}{{- end -}}
{{- if eq (include "memex.adoNetClustering" .) "true" -}}
{{- $orleans := include "memex.dbHostGroup" (include "memex.orleansConnectionString" .) -}}
{{- if $orleans -}}{{- $targets = append $targets $orleans -}}{{- end -}}
{{- end -}}
{{- $targets = $targets | uniq -}}
{{- if not $targets -}}
{{- fail (printf "memex.dbProbeTargets: no database host could be derived for '%s'. Its connection strings name no Host= (or Server=), so the wait-for-postgres init container has nothing to wait for — and an init container that probes nothing would PASS, which is the exact failure #4173 removes. Set secrets.%s.ConnectionStrings__memex to a connection string naming a host." .half .half) -}}
{{- end -}}
{{- join " " $targets -}}
{{- end -}}

{{- /* The init-container shell command that waits for every endpoint in `memex.dbProbeTargets`.
       One place, so the portal and the migration cannot wait for different things; it names each
       endpoint as it waits, so the log says WHICH host is not answering rather than only that
       something is not. */ -}}
{{- define "memex.waitForDatabasesCommand" -}}
{{- printf "for g in %s; do p=${g##*:}; hs=${g%%:*}; while true; do for h in $(echo $hs | tr ',' ' '); do nc -z $h $p && { echo 'postgres ready at '$h:$p; break 2; }; done; echo 'waiting for postgres at '$hs:$p; sleep 2; done; done" (include "memex.dbProbeTargets" .) -}}
{{- end -}}
