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
       Only meaningful under AdoNet; the caller asks `memex.adoNetClustering` first. */ -}}
{{- define "memex.orleansConnectionString" -}}
{{- $secrets := index .root.Values.secrets .half -}}
{{- $orleans := $secrets.ConnectionStrings__orleans -}}
{{- if and (not $orleans) $secrets.ConnectionStrings__memex -}}
{{- $orleans = printf "%s;Search Path=orleans" (trimSuffix ";" $secrets.ConnectionStrings__memex) -}}
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

{{- /* Every DISTINCT host GROUP the half's process will open, space-separated — the mesh database
       always, and the orleans one whenever AdoNet clustering is configured. Deduplicated, because
       the derived orleans string names the same server as the mesh one and probing it twice says
       nothing extra.

       🚨 Fails the render when no host could be derived. See the header. */ -}}
{{- define "memex.dbProbeTargets" -}}
{{- $targets := list -}}
{{- $mesh := include "memex.dbHostGroup" (include "memex.meshConnectionString" .) -}}
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
