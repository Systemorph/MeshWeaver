# Deploying portal-next (memex-local / helm)

The Next.js portal runs as its own Deployment beside `memex-portal`, and the existing portal
ingress gains one extra path: `/next` → this service.

## Graduated into the chart (feature-flagged) — the normal path

This is now a first-class, **feature-flagged** part of `deploy/helm`, off by default so prod/AKS is
unaffected:

- `deploy/helm/templates/memex-portal/portal-next.yaml` — the Deployment + Service.
- `deploy/helm/templates/memex-portal/ingress.yaml` — adds the `/next` path (gated).
- `deploy/helm/templates/memex-portal/config.yaml` — injects `Portal__ReactAppUrl: "/next"` (gated),
  which lights up the reversible per-user toggle (the Blazor user menu's *"Try the new frontend"* →
  `GET /frontend/react`; *"Back to classic"* → `/frontend/blazor`). The **default frontend stays
  Blazor** — enabling the flag is opt-in per user, not a forced switch.
- Chart values (`deploy/helm/values.yaml` → `portalNext`): `enabled` (default `false`), `image`,
  `replicas`, `portalOrigin`.

**Local (`memex.localhost`):** `memex-local up` / `memex-local update` build this image into Colima's
Docker store and `helm_deploy` flips `portalNext.enabled=true` automatically whenever the image is
present — no manual steps.

**AKS / other:** build + push the image to a registry the cluster can pull, then set
`portalNext.enabled=true` and `portalNext.image=<ref>` in your env overlay.

## Manual / ad-hoc manifests (reference)

The snippets below are the equivalent raw manifests you can `kubectl apply` into the same namespace
for a one-off experiment — kept for reference; the chart above is the source of truth.

## 1. Image

```bash
# grpc-web/src/gen (protobuf TS) is GENERATED + gitignored, and the .proto lives OUTSIDE the
# clients/ context (src/MeshWeaver.Hosting.Grpc/Protos), and buf ships glibc binaries — so
# generate on the HOST first, then build (see the Dockerfile header):
npm --prefix clients/grpc-web install && npm --prefix clients/grpc-web run gen

# From the repo root — the build context is clients/ (the app compiles ../react + ../grpc-web sources)
docker build -f clients/portal-next/Dockerfile -t meshweaver/portal-next:dev clients/
```

## 2. Deployment + Service

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: portal-next-deployment
  labels: { app.kubernetes.io/component: portal-next }
spec:
  replicas: 1
  selector:
    matchLabels: { app.kubernetes.io/component: portal-next }
  template:
    metadata:
      labels: { app.kubernetes.io/component: portal-next }
    spec:
      containers:
        - name: portal-next
          image: meshweaver/portal-next:dev
          ports: [{ containerPort: 3000 }]
          env:
            # Server-side token mint + snapshot fetch go straight to the portal Service,
            # skipping the ingress round-trip. The BROWSER still talks same-origin
            # (cookies + gRPC-web are ingress-routed), so this only affects SSR.
            - name: PORTAL_ORIGIN
              value: "http://memex-portal-service:8080"
          resources:
            requests: { cpu: 50m, memory: 128Mi }
            limits: { memory: 512Mi }
          readinessProbe:
            httpGet: { path: /next, port: 3000 }
          livenessProbe:
            httpGet: { path: /next, port: 3000 }
---
apiVersion: v1
kind: Service
metadata:
  name: portal-next-service
spec:
  selector: { app.kubernetes.io/component: portal-next }
  ports: [{ port: 3000, targetPort: 3000 }]
```

## 3. Ingress path

The chart's portal ingress (`deploy/helm/templates/memex-portal/ingress.yaml`) routes `/` to
`memex-portal-service`; add the `/next` prefix BEFORE it on the same host rule (nginx matches the
longest prefix first regardless of order, but keeping it first reads correctly):

```yaml
          - path: "/next"
            pathType: "Prefix"
            backend:
              service:
                name: "portal-next-service"
                port: { number: 3000 }
          # existing:
          - path: "/"
            pathType: "Prefix"
            backend:
              service:
                name: "memex-portal-service"
                port: { number: 8080 }
```

The app is built with `basePath: "/next"`, so no rewrite annotation is needed — it expects the
prefix. TLS terminates at the ingress as today; the portal session cookie flows to `/next/*`
because it is the SAME host (cookie path `/`), which is what authorizes the per-request
server-side token mint.

## Notes

- **Stateless by design**: every request mints a short-lived token from the forwarded cookies and
  fetches a REST snapshot; the server never opens a gRPC/stream subscription. Scale horizontally
  at will; no sticky sessions.
- The live layer is browser-only (same-origin `/api/tokens` + gRPC-web at the origin root),
  identical to the Vite SPA at `/app`.
- `/frontend/blazor` ("Back to classic") and `/frontend/react` remain portal endpoints — the
  `mw-frontend` cookie toggle works unchanged on the shared host.
