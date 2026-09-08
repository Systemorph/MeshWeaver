{{- /*
The fleet's own container registry (`cr.meshweaver.cloud`) — a SEPARATE service beside the portal,
built from two off-the-shelf images and zero registry code of ours:

  * distribution   the CNCF reference registry (ghcr.io/distribution/distribution), storage driver
                   `azure` authenticating as the namespace's workload identity, token auth pointed
                   at the auth server below;
  * docker_auth    cesanta/docker_auth — the token server. ONE static publisher account (its
                   bcrypt hash is the only credential in values, and a hash is committable) plus an
                   `ext_auth` hook that validates ANY other password as a MeshWeaver instance key
                   by asking the portal's plugin catalog (`registry.validationUrl`).

Both share the one host through ONE Ingress: `/auth` → docker_auth, everything else → distribution.
Design: Doc/Architecture/ContainerRegistryInMemex → "The registry as a separate service".

🚨 THIS HELPER IS THE GATE. Every template under templates/registry/ reads its values through
`memex.registry` and nothing else, so a half-declared block fails `helm template` HERE, naming the
key — never a registry that renders, deploys and then cannot start (a distribution pod whose token
bundle is an empty mount reports "no such file" from inside the container, not from helm), or
worse, one that starts with anonymous push because the ACL referenced a publisher with no hash.

Rendered ONLY when `registry.enabled`, so every environment that has not opted in renders
byte-identically — the chart's neutrality rule.

NAMES ONLY. No secret VALUE is in values or in any render: the token certificate and key, the
HTTP secret and the notification bearer are Key Vault OBJECT NAMES the CSI driver fetches at pod
start. The one exception is deliberate and safe: `publisher.passwordBcrypt` is a bcrypt HASH.
*/ -}}
{{- define "memex.registry" -}}
{{- $r := (.Values.registry | default dict) -}}
{{- $kv := ($r.keyVault | default dict) -}}
{{- $fallback := (.Values.keyVaultSecrets | default dict) -}}
{{- $storage := ($r.storage | default dict) -}}
{{- $publisher := ($r.publisher | default dict) -}}
{{- $notifications := ($r.notifications | default dict) -}}
{{- $ingress := ($r.ingress | default dict) -}}
{{- $resources := ($r.resources | default dict) -}}
{{- $host := required "registry.host is required when registry.enabled — the public hostname the registry answers on (e.g. cr.meshweaver.cloud)" $r.host -}}
{{- $image := required "registry.image is required when registry.enabled — the distribution image, pinned by digest (ghcr.io/distribution/distribution:3.1.1@sha256:…)" $r.image -}}
{{- $authImage := required "registry.authImage is required when registry.enabled — the cesanta/docker_auth image, pinned by digest" $r.authImage -}}
{{- $accountName := required "registry.storage.accountName is required when registry.enabled — the Azure Storage account holding the registry's blobs" $storage.accountName -}}
{{- $vaultName := required "registry.keyVault.name is required when registry.enabled — the Key Vault holding the token certificate, key and HTTP secret" $kv.name -}}
{{- $tenantId := required "registry.keyVault.tenantId is required when registry.enabled (or keyVaultSecrets.tenantId, which it falls back to)" ($kv.tenantId | default $fallback.tenantId) -}}
{{- $identityClientId := required "registry.keyVault.identityClientId is required when registry.enabled (or keyVaultSecrets.identityClientId) — the Key Vault Secrets Provider add-on's user-assigned identity client id" ($kv.identityClientId | default $fallback.identityClientId) -}}
{{- $certObject := required "registry.keyVault.certObject is required when registry.enabled — the vault secret holding the token-signing certificate (PEM)" $kv.certObject -}}
{{- $keyObject := required "registry.keyVault.keyObject is required when registry.enabled — the vault secret holding the token-signing private key (PEM)" $kv.keyObject -}}
{{- $httpSecretObject := required "registry.keyVault.httpSecretObject is required when registry.enabled — the vault secret holding distribution's http.secret; without a shared one every replica signs upload state differently and chunked pushes fail across pods" $kv.httpSecretObject -}}
{{- $passwordBcrypt := required "registry.publisher.passwordBcrypt is required when registry.enabled — the publisher account's bcrypt HASH (htpasswd -nB <user>); without it the ACL would name an account nobody can authenticate as" $publisher.passwordBcrypt -}}
{{- $notificationUrl := ($notifications.url | default "") -}}
{{- $notificationSecretObject := ($kv.notificationSecretObject | default "") -}}
{{- if and $notificationUrl (not $notificationSecretObject) -}}
{{- fail "registry.keyVault.notificationSecretObject is required when registry.notifications.url is set — the vault secret holding the Authorization header value the registry sends with every event" -}}
{{- end -}}
{{- $out := dict
      "host" $host
      "image" $image
      "authImage" $authImage
      "issuer" ($r.issuer | default "memex-registry")
      "replicas" (int ($r.replicas | default 1))
      "serviceAccount" ($r.serviceAccount | default "memex-portal-sa")
      "accountName" $accountName
      "container" ($storage.container | default "registry")
      "vaultName" $vaultName
      "tenantId" $tenantId
      "identityClientId" $identityClientId
      "certObject" $certObject
      "keyObject" $keyObject
      "httpSecretObject" $httpSecretObject
      "notificationSecretObject" $notificationSecretObject
      "publisherUsername" ($publisher.username | default "publisher")
      "publisherPasswordBcrypt" $passwordBcrypt
      "validationUrl" ($r.validationUrl | default "https://memex.meshweaver.cloud/api/plugins?ref=HEAD")
      "notificationUrl" $notificationUrl
      "ingressClassName" ($ingress.className | default "nginx")
      "clusterIssuer" ($ingress.clusterIssuer | default "")
      "tlsSecret" ($ingress.tlsSecret | default "memex-registry-tls")
      "registryResources" ($resources.registry | default (dict "requests" (dict "cpu" "50m" "memory" "128Mi") "limits" (dict "memory" "512Mi")))
      "authResources" ($resources.auth | default (dict "requests" (dict "cpu" "20m" "memory" "32Mi") "limits" (dict "memory" "128Mi")))
-}}
{{- toYaml $out -}}
{{- end -}}
