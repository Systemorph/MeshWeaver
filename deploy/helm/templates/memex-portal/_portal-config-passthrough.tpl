{{- /*
memex.portalConfigPassThrough — the second half of the portal ConfigMap's data (config.yaml).

Renders every config.memex_portal key that the LITERAL half (memex.portalConfigData) did not
render, verbatim, so a key a Deployment record declares (extraPortalConfig → config.memex_portal)
reaches the container even when this chart has no line for it. Called with
  (dict "root" <the root context> "literal" <the literal half, rendered>)

WHY. The literal half names every key it knows explicitly. A key it does not name used to reach NO
container and helm reported success: #1778, #1780, #1925, #2203 in values files, and on records
Memex#689 (Hosting__PrBabysitter__Relay: "false", reverted as inert in Memex#693) and memex.systemorph.com's
Hosting__RecordChangeReconcile__Enabled off-switch (Memex#690, closed for the same reason).

THE RULES, in order, per config.memex_portal key:
  1. The literal half rendered it → skip. Its default, trim or computed value wins, and the
     ConfigMap can never carry the same data key twice. "Rendered" is read from the literal half's
     OUTPUT (fromYaml), so this list cannot drift from the template.
  2. A null or blank value → skip — the same "rendered only when set" rule the literal switches use
     (a present key with a YAML null would otherwise render "<nil>", and a blank would override a
     code default with "").
  3. REFUSED, failing the render by name:
       • a key that differs only by CASE from a key the literal half rendered — .NET folds env keys,
         so the two would race for one configuration value on every pod start;
       • a key that is not a valid ConfigMap key ([-._a-zA-Z0-9]+);
       • a map or a list — an environment variable carries one string;
       • a Modules__Required__N slot the literal block does not carry (outside 0..19). That block's
         ceiling is a contract MeshWeaver.Deployment.Contract's ChartModuleSlotProblems states and
         a Deployment record's plan already refuses; passing the slot through would make the chart
         and that rule disagree, so the chart refuses it too.
  4. Otherwise → rendered as `<key>: "<value | toString | trim>"` (a whole number without a decimal
     point, so 3 stays "3" rather than becoming "3e+00").
*/ -}}
{{- define "memex.portalConfigPassThrough" -}}
{{- $root := .root -}}
{{- $rendered := .literal | fromYaml | default dict -}}
{{- if hasKey $rendered "Error" -}}
{{- fail (printf "memex-portal-config: the literal half of the ConfigMap data did not parse as YAML, so the pass-through cannot tell which keys it rendered: %v" (get $rendered "Error")) -}}
{{- end -}}
{{- $folded := dict -}}
{{- range $k, $_ := $rendered -}}
{{- $_ := set $folded (lower $k) $k -}}
{{- end -}}
{{- $config := ($root.Values.config).memex_portal | default dict -}}
{{- range $key := keys $config | sortAlpha -}}
{{- $value := get $config $key -}}
{{- if not (hasKey $rendered $key) -}}
{{- if hasKey $folded (lower $key) -}}
{{- fail (printf "config.memex_portal.%s differs only by case from the chart key %s — .NET folds environment keys case-insensitively, so the two would race for one configuration value; use the chart's spelling" $key (get $folded (lower $key))) -}}
{{- end -}}
{{- if not (regexMatch "^[-._a-zA-Z0-9]+$" $key) -}}
{{- fail (printf "config.memex_portal.%s is not a valid ConfigMap key ([-._a-zA-Z0-9]+), so it cannot reach the container" $key) -}}
{{- end -}}
{{- if or (kindIs "map" $value) (kindIs "slice" $value) -}}
{{- fail (printf "config.memex_portal.%s is a %s — an environment variable carries one string; flatten it to Section__Key entries" $key (kindOf $value)) -}}
{{- end -}}
{{- if not (kindIs "invalid" $value) -}}
{{- $text := $value | toString | trim -}}
{{- if and (kindIs "float64" $value) (eq (float64 (int64 $value)) $value) -}}
{{- $text = $value | int64 | toString -}}
{{- end -}}
{{- if ne $text "" -}}
{{- if hasPrefix "modules__required__" (lower $key) -}}
{{- fail (printf "config.memex_portal.%s is a boot-module slot the chart's literal Modules__Required__N block does not carry (it renders 0..19) — raise that block and MaxChartRenderedRequiredModuleSlot together, or move the module into the contiguous list" $key) -}}
{{- end }}
  {{ $key }}: {{ $text | quote }}
{{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}
