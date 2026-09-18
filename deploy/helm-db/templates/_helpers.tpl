{{- /* Refuse a database release that cannot work, naming the input — never render a Cluster that
       schedules nowhere, bootstraps a database with no name, or archives WAL to no destination. */ -}}
{{- define "memexdb.validate" -}}
{{- if not (regexMatch "^[a-z_][a-z0-9_]{0,62}$" (toString .Values.database)) -}}
{{- fail (printf "memex-db: 'database' is '%s' — it must be a plain lower-case PostgreSQL identifier (the Hosting/Deployment record's `database`). Refusing to bootstrap a database with no usable name." (toString .Values.database)) -}}
{{- end -}}
{{- if not (regexMatch "^[a-z_][a-z0-9_]{0,62}$" (toString .Values.owner)) -}}
{{- fail (printf "memex-db: 'owner' is '%s' — it must be a plain lower-case PostgreSQL role name." (toString .Values.owner)) -}}
{{- end -}}
{{- if lt (int .Values.instances) 1 -}}
{{- fail "memex-db: 'instances' must be at least 1." -}}
{{- end -}}
{{- if not .Values.storage.storageClass -}}
{{- fail "memex-db: 'storage.storageClass' is empty — the cluster's default class is not zonal Premium SSD v2, so it must be named (the platform installs memex-db-premiumv2)." -}}
{{- end -}}
{{- if not (has .Values.placement.zoneAntiAffinity (list "required" "preferred")) -}}
{{- fail (printf "memex-db: 'placement.zoneAntiAffinity' is '%s' — it must be 'required' or 'preferred'." (toString .Values.placement.zoneAntiAffinity)) -}}
{{- end -}}
{{- if .Values.backup.enabled -}}
{{- if not (hasPrefix "https://" (toString .Values.backup.destinationPath)) -}}
{{- fail "memex-db: backup.enabled is true but backup.destinationPath is not an https:// Blob container URL — WAL would be archived nowhere, and a database that believes it is backed up is worse than one that knows it is not." -}}
{{- end -}}
{{- if not .Values.backup.workloadIdentityClientId -}}
{{- fail "memex-db: backup.enabled is true but backup.workloadIdentityClientId is empty — the Barman Cloud plugin authenticates to Blob as the pods' workload identity; there is no storage key in this chart by design." -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "memexdb.labels" -}}
app.kubernetes.io/name: memex-db
app.kubernetes.io/instance: {{ .Release.Name | quote }}
app.kubernetes.io/component: database
app.kubernetes.io/managed-by: {{ .Release.Service | quote }}
{{- end -}}
