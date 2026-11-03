# Gazetteer / Personal Cluster Only

This repo deploys to Marcus's home **microk8s** cluster only (namespace `default`).

## Hard rules
- NEVER deploy, apply, or push to work clusters. Forbidden contexts include
  `artio-*`, `bidwork-*`, or any Azure-hosted AKS cluster.
- Allowed kubectl/helm context: `microk8s` only. Before any `kubectl apply`,
  `helm upgrade`/`install`, `kubectl rollout`, or image push, run
  `kubectl config current-context` and confirm it is `microk8s`. If it isn't, stop and ask.

## Deploying
- CI (`.github/workflows/docker-build.yml`) builds and pushes
  `gluteusmaximus/{gazetteer-api,gazetteer-web,gazetteer-seeder}` to Docker Hub
  on every push to `master`, tagged `latest` and `<git-sha>`. Check
  `gh run list --workflow=docker-build.yml` before building anything locally —
  the image is usually already built.
- The Helm chart lives in a separate repo: `C:\Data\Bongo\BongoFms\charts\gazetteer`.
  The live release is named `gazetteer` in namespace `default`.
- **Image tag is pinned to `latest`** in the chart (`api.image.tag`,
  `web.image.tag`) — a plain `helm upgrade` will NOT trigger a new pull, since
  the Deployment spec doesn't change. To actually roll a newly-pushed image,
  follow up with `kubectl rollout restart deployment/gazetteer-api` and/or
  `deployment/gazetteer-web` (imagePullPolicy is `Always`, so the restart
  re-pulls `latest`).
- `gazetteer-seeder` is a one-off Job (not a live Deployment) that loads
  location data into Postgres/Elasticsearch — don't re-trigger it casually,
  it's expensive (large dataset download/parse/index).

## `helm upgrade --reuse-values` gotcha
`--reuse-values` is not a safe no-op just because you skip `--set`. It carries
forward the *previous release revision's full computed values* — including
any stale override that was set long ago and never actually exercised.

**Incident (2026-07-21):** a routine `helm upgrade gazetteer <chart>
--reuse-values` (meant as a harmless reconcile) recreated the
`gazetteer-elasticsearch` StatefulSet pod. `--reuse-values` pulled forward a
stale `elasticsearch.javaOpts=-Xms2g -Xmx2g` from an older revision — the
chart's on-disk default was already a safe `512m`, but that only applies to
values that were never explicitly set. The pod had simply never restarted
since the bad value was deployed, so it went unnoticed. The 2g heap exceeded
the container's hardcoded `1Gi` memory limit → immediate OOMKill crash loop →
Gazetteer search outage until the heap was fixed and the API pod's stale ES
connection pool was cleared with `kubectl rollout restart deployment/gazetteer-api`.

**Before running `--reuse-values`:** run `helm get values gazetteer -n default
--all` first and sanity-check every computed value (not just the
"USER-SUPPLIED VALUES" view, which hides inherited-but-stale overrides)
against current resource limits, especially for the Elasticsearch
StatefulSet's memory-sensitive `javaOpts`. Override explicitly with `--set`
if anything looks risky. Also assume any Helm upgrade — even a values-only
one — can force a pod recreation, and watch the affected pods afterward.
