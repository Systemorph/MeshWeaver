{{- /*
Crash dumps (values: crashDumps, persistence.dumps). Doc/Architecture/DebuggingNativeCrashes →
"Production: where a dump lands, and how long it stays".

memex.crashDumps — the resolved settings, as YAML (fromYaml it):
  root           the dump root. MUST be a normalised path strictly below /data
                 (^/data(/segment)+$, no segment starting with "."), so `..`, `.`, `//` and /data
                 itself are refused — retention deletes files under it.
  claim          the dedicated dump claim (persistence.dumps.claimName), or "" for the /data volume.
                 A dedicated claim is mounted AT the root, so the worst a dump can fill is that
                 claim; without one the dump shares the /data claim (DataProtection keys, assembly
                 cache) and the headroom gate is the only bound.
  headroomDumps  how many heap dumps the free space must hold before a pod arms its directory.
                 On the shared /data claim it defaults to the replica CEILING (keda.maxReplicas
                 under KEDA, else replicas.portal, else 1): every pod that could crash at the same
                 moment is counted. On a dedicated claim it defaults to 1.
*/ -}}
{{- define "memex.crashDumps" -}}
{{- $cd := .Values.crashDumps | default dict -}}
{{- $root := $cd.root | default "/data/dumps" -}}
{{- if not (regexMatch "^/data(/[A-Za-z0-9_-][A-Za-z0-9._-]*)+$" $root) -}}
{{- fail (printf "crashDumps.root %q must be a normalised directory strictly below /data (no '..', '.', '//' or trailing '/'): a root anywhere else would not outlive the pod, and retention deletes files under it (Doc/Architecture/DebuggingNativeCrashes)" $root) -}}
{{- end -}}
{{- $ceiling := 1 -}}
{{- if (.Values.keda).enabled -}}
{{- $ceiling = int ((.Values.keda).maxReplicas | default 1) -}}
{{- else if (.Values.replicas).portal -}}
{{- $ceiling = int .Values.replicas.portal -}}
{{- end -}}
{{- /* On a dedicated claim a dump can only fill the dump claim, so one dump's room is enough to
       avoid writing a truncated one. On the shared /data claim every possible writer is counted. */ -}}
{{- if ((.Values.persistence).dumps).claimName -}}
{{- $ceiling = 1 -}}
{{- end -}}
root: {{ $root | quote }}
claim: {{ ((.Values.persistence).dumps).claimName | default "" | quote }}
keep: {{ $cd.keep | default 3 | quote }}
maxAgeDays: {{ $cd.maxAgeDays | default 14 | quote }}
activeMinutes: {{ $cd.activeMinutes | default 30 | quote }}
headroomDumps: {{ $cd.headroomDumps | default $ceiling | quote }}
reserveMiB: {{ $cd.reserveMiB | default 2048 | quote }}
{{- end -}}

{{- /* The env both the init container and the portal container carry. `container` names the
       container whose memory limit sizes a dump (the init container reads the PORTAL's). */ -}}
{{- define "memex.crashDumpEnv" -}}
{{- $d := include "memex.crashDumps" .root | fromYaml -}}
- name: "MEMEX_POD_NAME"
  valueFrom:
    fieldRef:
      fieldPath: "metadata.name"
- name: "MEMEX_MEMORY_LIMIT_BYTES"
  valueFrom:
    resourceFieldRef:
      containerName: "memex-portal"
      resource: "limits.memory"
      divisor: "1"
- name: "MEMEX_CRASHDUMP_ROOT"
  value: {{ $d.root | quote }}
- name: "MEMEX_CRASHDUMP_KEEP"
  value: {{ $d.keep | quote }}
- name: "MEMEX_CRASHDUMP_MAX_AGE_DAYS"
  value: {{ $d.maxAgeDays | quote }}
- name: "MEMEX_CRASHDUMP_ACTIVE_MINUTES"
  value: {{ $d.activeMinutes | quote }}
- name: "MEMEX_CRASHDUMP_HEADROOM_DUMPS"
  value: {{ $d.headroomDumps | quote }}
- name: "MEMEX_CRASHDUMP_RESERVE_MIB"
  value: {{ $d.reserveMiB | quote }}
- name: "MEMEX_CRASHDUMP_PHASE"
  value: {{ .phase | quote }}
{{- end -}}

{{- /* The mounts both containers need: /data, and the dedicated dump claim at the root when one is
       declared (mounted AFTER /data, over the root directory). */ -}}
{{- define "memex.crashDumpMounts" -}}
{{- $d := include "memex.crashDumps" . | fromYaml -}}
- name: "memex-data"
  mountPath: "/data"
{{- if $d.claim }}
- name: "memex-crashdumps"
  mountPath: {{ $d.root | quote }}
{{- end }}
{{- end -}}
