{{/*
🚨 THE PER-KEY REQUIRED-SECRET PREDICATE (Systemorph/Memex#204).

The defect it closes: a Secret key rendered with an EMPTY value is INVISIBLE to a `keys[]` audit —
it reads as configured. `MEMEX_PASSWORD` sat at base64 length 0 on memex-cloud in both
`memex-portal-secrets` and `memex-migration-secrets` for exactly that reason. An unconfigured
credential then fails at CONNECT time, in a running portal, instead of at render time where the
operator is standing.

🚨 DELIBERATELY NOT a blanket `required`. Which optional providers a given deployment must have is
POLICY, and it varies by deployment shape and role — a demo instance legitimately runs with no
Anthropic key, and `Ai__KeyProtection__MasterKey` is worse than that: its legality is STATEFUL
(fine until the first `enc:` provider key is stored, fatal after). A chart-wide `required` would
encode one deployment's policy as everyone's, and #204 names those calls as the maintainer's.
So this ships as a MECHANISM with an EMPTY default list: it enforces exactly what an operator
declares, and nothing at all until they declare something.

Declare in values (or an env values file):

    requiredSecrets:
      - key: Anthropic__ApiKey
        because: this deployment routes the `heavy` tier to Anthropic; without it every agent round
                 fails at the provider call with no configuration error anywhere.
      - key: memex_postgres_password
        component: memex_migration      # default: memex_portal
      - key: GitHub__App__PrivateKey
        when: false                     # see the note on `when` below

Fields:
  key        (required) the values key under `secrets.<component>`, i.e. the name the Secret renders.
  because    (optional) why THIS deployment needs it. Quoted verbatim in the failure, because a
             refusal that does not say why gets worked around rather than fixed.
  component  (optional) `memex_portal` (default) or `memex_migration`.
  when       (optional) a BOOLEAN, default true. 🚨 Helm cannot evaluate an expression here — there
             is no `eval`. So `when` is a value an env values file SETS (e.g. `when: false` to stand
             an entry down for one environment) rather than a condition the chart computes. Anything
             needing a computed condition belongs in the caller's values, not in a string here.

An entry naming a key that is empty (or absent) fails the render, naming the key, the component and
the reason. Empty list ⇒ this template does nothing, which is the shipped default.
*/}}
{{- define "memex.assertRequiredSecrets" -}}
{{- $root := . -}}
{{- range $i, $entry := (.Values.requiredSecrets | default list) -}}
  {{- if not $entry.key -}}
    {{- fail (printf "requiredSecrets[%d] has no `key`. Each entry must name the values key under secrets.<component> that has to be non-empty; an entry without one can never be checked and would pass silently, which is the shape #204 exists to refuse." $i) -}}
  {{- end -}}
  {{- $component := $entry.component | default "memex_portal" -}}
  {{- if not (has $component (list "memex_portal" "memex_migration")) -}}
    {{- fail (printf "requiredSecrets[%d] (key %s) names component %q, which is not a secrets block this chart renders. Use `memex_portal` (default) or `memex_migration`." $i $entry.key $component) -}}
  {{- end -}}
  {{- $when := true -}}
  {{- if hasKey $entry "when" -}}{{- $when = $entry.when -}}{{- end -}}
  {{- if $when -}}
    {{- $block := index $root.Values.secrets $component | default dict -}}
    {{- $value := index $block $entry.key -}}
    {{- if not $value -}}
      {{- $because := $entry.because | default "no reason was recorded on the requiredSecrets entry" -}}
      {{- fail (printf "secrets.%s.%s is required by this deployment and is empty or unset.\n\nWhy: %s\n\n🚨 An empty value is NOT the same as an absent one: the chart would render the key present-but-empty, an audit that enumerates keys[] would read it as configured, and the credential would fail at CONNECT time inside a running portal instead of here (Systemorph/Memex#204). Set it in this release's values (or supply it out-of-band and drop this requiredSecrets entry), then re-run." $component $entry.key $because) -}}
    {{- end -}}
  {{- end -}}
{{- end -}}
{{- end -}}
