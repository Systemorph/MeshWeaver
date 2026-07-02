# Deploying portal-next (memex-local / helm)

The Next.js portal runs as its own Deployment beside `memex-portal`, and the existing portal
ingress gains one extra path: `/next` → this service. Nothing in `deploy/` needs to change for a
local experiment — the snippets below are additive manifests you can `kubectl apply` into the
same namespace (or fold into `deploy/helm` as a `portal-next` template when it graduates).

## 1. Image

```bash
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
