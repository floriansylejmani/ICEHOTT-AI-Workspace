# Phase 7A — Production Environment & Release Foundation

**Baseline:** `c63a6b2` (Phase 6A merged)
**Parent architecture:** [PHASE-7-PRODUCTION-DEPLOYMENT-SRE.md](PHASE-7-PRODUCTION-DEPLOYMENT-SRE.md)

Phase 7A delivers the code, container, migration and pipeline foundation for production. It does **not** create or modify any live Vercel, Railway, database or registry resource, and no production deploy has happened. Sections marked *manual setup* list what an account owner must still do.

## 1. What 7A adds

| Area | Implementation |
|---|---|
| Fail-closed configuration | `ICEHOTT.API/Hosting/ProductionConfigurationValidator.cs`, called first in `Program.cs` |
| Service roles | `ServiceRole` (`Api`, `Worker`, `All`), `ServiceRoles`, `BackgroundServiceRegistration` |
| Release identity | `ReleaseInfo` and `GET /release` |
| Proxy trust | `ForwardedHeadersSetup` (trusted-network forwarded headers) |
| Production defaults | `appsettings.Production.json` (non-secret only) |
| Container | `backend/Dockerfile`: non-root runtime, `migrator` target, Git SHA build arg |
| Migrations | Design-time factory + self-contained EF bundle image (`--target migrator`) |
| Pipelines | `ci.yml` job `release-foundation`, `deploy-staging.yml`, `promote-production.yml` |
| Scripts | `scripts/release/{smoke,verify-container,deploy-railway}.sh`, `validate_deploy_config.py` |

Out of scope (later subphases): S3 object storage (7B), OpenTelemetry and alerts (7C), backup/restore and DR (7D), load tests and the final release gate (7E).

## 2. Environments

| | Development | Staging | Production |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` (or `Staging`) | `Production` |
| `Deployment__Tier` | unset (or `local`) | `staging` | `production` |
| Startup validation | not applied | **enforced** | **enforced** |
| `Service__Role` | unset means `All` | `Api` or `Worker`, explicit | `Api` or `Worker`, explicit |
| Auto-migration | opt-in (`Database__AutoMigrate`) | forbidden | forbidden |
| Artifact storage | Local filesystem | object storage (see 7B note) | object storage (see 7B note) |

Validation applies when the ASP.NET environment is `Production`/`Staging` **or** `Deployment__Tier` is `staging`/`production`. A `Development` host carrying a hosted tier is rejected. Docker Compose stays Development-only and unchanged in behavior (it runs the image as root only so pre-existing named volumes stay writable).

## 3. Startup validation

The process exits with status 1 and prints a summary such as `Jwt:Key: matches a known development or placeholder value.` Messages contain configuration **key names and problem categories only**, never values, and parser exceptions are never echoed (tests assert this with sentinel secrets).

Rejected in staging/production:

- ASP.NET environment `Development`; missing/unknown `Deployment:Tier`.
- `Service:Role` unset, `All` or unknown.
- `Release:GitSha` missing or not the exact **40-character** commit SHA (abbreviated SHAs are tolerated only for local builds).
- Database connection missing or malformed; empty or well-known password (`change-me`, `postgres`, `password`, `icehott`, `admin`, `root`, `example`); user `postgres`; loopback host; SSL mode below `Require` while `Database:RequireTransportSecurity` is true (the default).
- `Database:AutoMigrate` true, or any unrecognised boolean value.
- `ArtifactStorage:Provider` unset, or `Local` without the deliberate `ProductionSafety:AllowLocalArtifactStorage=true` override; any non-local provider without `ObjectStorage:Endpoint` and `ObjectStorage:Bucket`.
- `AiRuntime:BaseUrl` not absolute or loopback.
- **API role only** (the worker exposes none of this and is not required to hold or know it):
  - `Jwt:Key` missing, shorter than 32 characters, or containing a development/placeholder marker (`development-only`, `never-use-in-production`, `ci-only`, `replace-with`, `change-me`, `changeme`, `placeholder`, `example`).
  - CORS origins empty, wildcard, non-https or loopback; `AllowedHosts` empty or `*`.
  - `ForwardedHeaders:Enabled` not true, or `ForwardedHeaders:TrustedNetworks` empty, invalid, or `/0` (see section 5).

`appsettings.Production.json` supplies only safe defaults: `AutoMigrate=false`, `RequireTransportSecurity=true`, `ArtifactStorage:Provider=S3` (so production never silently falls back to disk), the local override off, empty CORS list, and quiet logging (EF SQL command logging at `Warning`). A test asserts the file has no secrets or localhost values and fails validation on its own.

## 4. Roles

One image, one setting: `Service__Role`.

- **Api** serves controllers, `/health`, `/ready` and `/release`. It registers **no** hosted services (no knowledge ingestion, artifact maintenance, tool-execution recovery, workflow runner or scheduler).
- **Worker** registers those durable services (subject to the existing `*:Enabled` flags) and serves only `/health` (platform liveness) and `/release`. API routes and `/ready` return 404. It needs no public ingress, and it starts **without** the JWT signing key, CORS origins, `AllowedHosts` and proxy-trust settings (least privilege: a worker compromise does not expose token-signing material). It still needs the database connection and the AI runtime URL.
- **All** does both; permitted only outside staging/production (local development, tests).

The PostgreSQL leasing/fencing model is untouched; this only decides which process runs which loop. An unknown value fails startup in every environment, without echoing the value.

## 5. Client address and scheme behind the proxy

ICEHOTT runs behind managed reverse proxies, so the socket peer is always the proxy. The code depends on the client address in one security-relevant place: the auth rate limiter partitions by `RemoteIpAddress`. Without forwarded-header handling every user would share one bucket of `RateLimiting:AuthPermitLimit` (default 10) requests per minute, i.e. a self-inflicted login outage. The refresh cookie `Secure` flag depends on the environment, not the request scheme; no absolute callback URLs are generated; and `UseHttpsRedirection` has no configured HTTPS port, so it never redirects. This was therefore treated as a 7A blocker and implemented:

- `ForwardedHeaders:Enabled=true` applies `X-Forwarded-For` and `X-Forwarded-Proto`, first in the pipeline (before rate limiting), **only from `ForwardedHeaders:TrustedNetworks`** (CIDR list). `ForwardLimit` defaults to 1, so only the entry appended by the trusted proxy is used and a client-supplied header chain cannot override it.
- Enabled with no trusted network, or with an invalid or `/0` network, fails startup rather than trusting every peer (which would let any client spoof its address and evade the limiter).
- The staging/production API role must enable it (validator). The platform proxy network must be supplied as `ForwardedHeaders__TrustedNetworks__0`; look it up on the platform (manual setup) and keep it narrow.

## 6. Release identity

`GET /release` returns `{ service, role, environment, gitSha, version }` and nothing else. `gitSha` comes from `Release__GitSha`, baked into the image from the `GIT_SHA` build argument (also stored as the `org.opencontainers.image.revision` label); invalid values are reported as `unknown`. `environment` is the deployment tier when set.

## 7. Container

`backend/Dockerfile` (build context: repository root):

- Restore layer copies project files only, then sources; the SDK is pinned by `global.json`.
- Runtime is `mcr.microsoft.com/dotnet/aspnet:9.0`, `ASPNETCORE_ENVIRONMENT=Production`, listening on 8080, running as the image's non-root `$APP_UID` (1654). No `.env`, secrets or source tree in the final image.
- `--target migrator` builds the self-contained migration bundle on `runtime-deps` (also non-root).

```bash
docker build -f backend/Dockerfile --build-arg GIT_SHA=$(git rev-parse HEAD) -t icehott-api:$(git rev-parse HEAD) .
docker build -f backend/Dockerfile --build-arg GIT_SHA=$(git rev-parse HEAD) --target migrator -t icehott-migrator:$(git rev-parse HEAD) .
```

Base images are pinned by tag (`9.0`), not digest; digest pinning is a possible later hardening.

## 8. Migrations

Normal startup never migrates in staging/production. Schema changes go through the bundle:

```bash
docker run --rm icehott-migrator:<sha> --connection "<migration connection string>"
```

- `DesignTimeDbContextFactory` lets the bundle build the context from a connection string alone; without it the bundle would boot the whole web host and demand JWT/CORS settings just to migrate. Because a design-time factory takes precedence over the web host, it resolves the connection the way the host would so local `dotnet ef` keeps working: `ICEHOTT_MIGRATION_CONNECTION`, then `ConnectionStrings__DefaultConnection` / `appsettings.json` / `appsettings.{Environment}.json` in the working directory (the API project when run through `dotnet ef`), then a never-dialled placeholder. The bundle's `--connection` overrides all of it at run time.
- The bundle is idempotent (a second run reports no migrations) and exits non-zero on failure, which stops the pipeline. There is no automatic downgrade.
- Use a migration credential separate from the runtime credential (`STAGING_MIGRATION_CONNECTION` / `PRODUCTION_MIGRATION_CONNECTION` secrets).
- **Expand/contract rule for future migrations:** an application version N must run against schema N and N+1. Add columns/tables/indexes first (nullable or defaulted), deploy code that tolerates both, and remove or tighten in a later release only after no running version depends on the old shape. Data migrations are forward-fixed, never auto-reverted.

## 9. Pipelines

**CI (`ci.yml`)** keeps the original backend/frontend/AI jobs and adds `release-foundation`: deployment-definition validation, shell syntax checks, production image + migrator + AI image builds, and `verify-container.sh`, which proves against the real images: non-root user and revision label, fail-closed start without configuration, migrations apply (count equals the repository's) and re-run as a no-op, bad credentials fail, the same image runs as `Api` and as `Worker` with only their own duties, and `smoke.sh` reports the expected SHA for both.

**Staging (`deploy-staging.yml`)** triggers only from a successful `push` CI run on `main` (or a manual run for a SHA already on `main` with green CI). It resolves the exact tested SHA, refuses commits not reachable from `main`, builds and verifies the images from that SHA, and then:

- **Dry run (default).** Without the repository variable `ICEHOTT_DEPLOY_STAGING=true` nothing is published or deployed, and the summary says so. The migrate, deploy and smoke jobs are skipped rather than pretending to succeed.
- **Enabled.** Publishes `icehott-{api,migrator,ai}:<full-sha>` to GHCR, migrates staging with the bundle, points the Railway services at those images, deploys the web preview, runs the smoke test and records the commit status `deploy/staging`.

**Production (`promote-production.yml`)** is `workflow_dispatch` only, with a required full 40-character `git_sha` and a typed confirmation. `verify` requires: SHA reachable from `main`, CI success, a `deploy/staging` success status for that SHA, and all three published images for that SHA. The `deploy/staging` status must have been **created by GitHub Actions** (a status posted with a personal token is rejected), and the API and migrator images must carry an `org.opencontainers.image.revision` label equal to the SHA (a tag alone is not accepted as provenance). **No container image is rebuilt**; migration and deploy use the staged images.

**Web artifact: rebuilt, not promoted.** Next.js inlines `NEXT_PUBLIC_*` values (the app reads `NEXT_PUBLIC_API_URL`) at build time, and staging and production necessarily differ, so a binary-identical promotion of the web bundle is not possible without changing the app. The guarantee is therefore weaker but exact: the same commit is built for each tier with a version-pinned Vercel CLI (`vercel@61.0.0`) and a frozen lockfile (`apps/web/vercel.json` sets `installCommand` to `npm ci`), and the web jobs fail if `NEXT_PUBLIC_API_URL` is not an `https` URL for that environment (an unset value would silently fall back to `http://localhost:5050`). The two builds are separate outputs from identical, lock-frozen inputs.

The smoke stage polls (up to 90 attempts, 2 s apart) until the instance is live **and** reports the expected SHA, because during a rolling deploy the previous release keeps answering. It checks the API; the worker has no ingress, so its release is not externally smoke-tested. `migrate`, `deploy`, `deploy-web` and `smoke` run in the `production` GitHub Environment, whose required reviewers are the approval gate. Smoke success records `deploy/production`. Mutable tags are rejected everywhere.

`validate_deploy_config.py` enforces these rules mechanically (manual-only production trigger, no `latest`, pinned Vercel CLI, no secret literals, no untrusted input in shell, staging dry-run gating, production environment on every stage, no image rebuild and no false "nothing is rebuilt" claim in promotion, provenance checks present, migration output not discarded, CI gates not removed). `test-smoke.sh` tests the smoke script against a local stub (rolling deploy, wrong release, dead service).

## 10. Rollback

Application rollback is a promotion (or staging deploy) of the previous known-good SHA: same images, same procedure. Because migrations follow expand/contract, the previous application version keeps working on the already-migrated schema. Schema is never rolled back automatically. Recording migration version and timestamp in release metadata, and a demonstrated rollback drill, belong to 7D/7E.

## 11. Manual setup still required

*Nothing below has been done or verified against a live account.*

1. **GitHub:** create Environments `staging` and `production`; add required reviewers to `production`. Set repository variable `ICEHOTT_DEPLOY_STAGING=true` only when ready. Ensure Actions may read/write GHCR packages for this repository (the images carry the `org.opencontainers.image.source` label for linking).
2. **Secrets (per environment):** `STAGING_MIGRATION_CONNECTION` / `PRODUCTION_MIGRATION_CONNECTION`, `RAILWAY_API_TOKEN` (scoped to one Railway environment), `VERCEL_TOKEN`.
3. **Variables:** `RAILWAY_{STAGING,PRODUCTION}_{ENVIRONMENT,API_SERVICE,WORKER_SERVICE,AI_SERVICE}_ID`, `VERCEL_ORG_ID`, `VERCEL_PROJECT_ID`, `STAGING_API_URL`, `PRODUCTION_API_URL`.
4. **Railway:** create separate staging and production environments with PostgreSQL + pgvector and Redis; create services `icehott-api` (public ingress) and `icehott-worker` (`Service__Role=Worker`, no ingress) from the GHCR image, `icehott-ai` (private), and configure GHCR pull credentials. Set runtime variables from section 11. Create a **project token** scoped to that one environment (sent as `Project-Access-Token`, not `Bearer`). **LIVE PROVIDER VERIFICATION REQUIRED** for `deploy-railway.sh`: the endpoint and the project-token header are confirmed against Railway documentation, but the mutations `serviceInstanceUpdate` / `serviceInstanceRedeploy` and their shapes were not, and the script has never run against an account. Introspect the schema with your token and verify before relying on it; it fails closed unless both mutations return `true`.
5. **Database:** create a non-superuser application role and a separate migration role; enable the `vector` extension; require TLS.
6. **Vercel:** create the project; set `NEXT_PUBLIC_*` public values only. The web app is the one artifact rebuilt per tier (from the same locked sources at the exact commit); it has no schema coupling.
7. **Domain/TLS:** point the public hostnames at Vercel and the API; set `AllowedHosts` and `Cors__AllowedOrigins__0` to them.

## 12. Required runtime variables

`Deployment__Tier`, `Service__Role`, `Release__GitSha` (baked into the image; must be the full 40-character SHA), `ConnectionStrings__DefaultConnection`, `AiRuntime__BaseUrl` (private service URL), `ArtifactStorage__Provider`, plus `ObjectStorage__Endpoint`/`__Bucket` for a non-local provider. **API role only:** `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience`, `AllowedHosts`, `Cors__AllowedOrigins__0`, `ForwardedHeaders__Enabled=true`, `ForwardedHeaders__TrustedNetworks__0` (the proxy network as CIDR). The worker needs none of the API-only settings. Optional: `OpenAiEmbedding__ApiKey`. Never commit values; inject them from Railway variables.

## 13. Known limits and deferred items

- **No object-storage provider yet (7B).** The Local provider is the only implementation, and startup rejects any other provider name. Until 7B ships, a real staging/production deployment can only start with `ArtifactStorage__Provider=Local` and the explicit, temporary `ProductionSafety__AllowLocalArtifactStorage=true` override (data would live on the container volume). This is deliberate: the safe default cannot start rather than silently using disk.
- Database TLS is required by default; a platform-private network without TLS needs the explicit `Database__RequireTransportSecurity=false`.
- The platform proxy network for `ForwardedHeaders__TrustedNetworks__0` must be looked up on the platform; it is deliberately not guessed. `UseHttpsRedirection` is unchanged and, with no HTTPS port configured, never redirects; TLS is terminated by the platform.
- `deploy-railway.sh` is **LIVE PROVIDER VERIFICATION REQUIRED** (mutation contract unconfirmed) and the Vercel steps are unverified against live accounts.
- The web bundle is rebuilt per tier (section 9), not promoted as a binary.
- The `deploy/staging` evidence is a GitHub commit status. Requiring `github-actions[bot]` as its creator blocks statuses posted with personal tokens, but anyone who can push a workflow to this repository can still make Actions post one, so the guarantee assumes trusted repository write access (protect `main`, restrict who can edit workflows, and keep required reviewers on the `production` environment).
- Not started: OpenTelemetry/alerts (7C), backup/restore/DR and rollback drill (7D), load and soak tests and the final release gate (7E), authenticated/tenant-isolation/artifact/workflow staging smoke (the current smoke checks liveness, readiness and release identity).
