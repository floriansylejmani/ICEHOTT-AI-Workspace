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
- `Release:GitSha` missing or not 7-64 hex characters.
- `Jwt:Key` missing, shorter than 32 characters, or containing a development/placeholder marker (`development-only`, `never-use-in-production`, `ci-only`, `replace-with`, `change-me`, `changeme`, `placeholder`, `example`).
- Database connection missing or malformed; empty or well-known password (`change-me`, `postgres`, `password`, `icehott`, `admin`, `root`, `example`); user `postgres`; loopback host; SSL mode below `Require` while `Database:RequireTransportSecurity` is true (the default).
- `Database:AutoMigrate` true, or any unrecognised boolean value.
- `ArtifactStorage:Provider` unset, or `Local` without the deliberate `ProductionSafety:AllowLocalArtifactStorage=true` override; any non-local provider without `ObjectStorage:Endpoint` and `ObjectStorage:Bucket`.
- `AiRuntime:BaseUrl` not absolute or loopback.
- API role only: CORS origins empty, wildcard, non-https or loopback; `AllowedHosts` empty or `*`.

`appsettings.Production.json` supplies only safe defaults: `AutoMigrate=false`, `RequireTransportSecurity=true`, `ArtifactStorage:Provider=S3` (so production never silently falls back to disk), the local override off, empty CORS list, and quiet logging (EF SQL command logging at `Warning`). A test asserts the file has no secrets or localhost values and fails validation on its own.

## 4. Roles

One image, one setting: `Service__Role`.

- **Api** serves controllers, `/health`, `/ready` and `/release`. It registers **no** hosted services (no knowledge ingestion, artifact maintenance, tool-execution recovery, workflow runner or scheduler).
- **Worker** registers those durable services (subject to the existing `*:Enabled` flags) and serves only `/health` (platform liveness) and `/release`. API routes and `/ready` return 404. It needs no public ingress.
- **All** does both; permitted only outside staging/production (local development, tests).

The PostgreSQL leasing/fencing model is untouched; this only decides which process runs which loop. An unknown value fails startup in every environment, without echoing the value.

## 5. Release identity

`GET /release` returns `{ service, role, environment, gitSha, version }` and nothing else. `gitSha` comes from `Release__GitSha`, baked into the image from the `GIT_SHA` build argument (also stored as the `org.opencontainers.image.revision` label); invalid values are reported as `unknown`. `environment` is the deployment tier when set.

## 6. Container

`backend/Dockerfile` (build context: repository root):

- Restore layer copies project files only, then sources; the SDK is pinned by `global.json`.
- Runtime is `mcr.microsoft.com/dotnet/aspnet:9.0`, `ASPNETCORE_ENVIRONMENT=Production`, listening on 8080, running as the image's non-root `$APP_UID` (1654). No `.env`, secrets or source tree in the final image.
- `--target migrator` builds the self-contained migration bundle on `runtime-deps` (also non-root).

```bash
docker build -f backend/Dockerfile --build-arg GIT_SHA=$(git rev-parse HEAD) -t icehott-api:$(git rev-parse HEAD) .
docker build -f backend/Dockerfile --build-arg GIT_SHA=$(git rev-parse HEAD) --target migrator -t icehott-migrator:$(git rev-parse HEAD) .
```

Base images are pinned by tag (`9.0`), not digest; digest pinning is a possible later hardening.

## 7. Migrations

Normal startup never migrates in staging/production. Schema changes go through the bundle:

```bash
docker run --rm icehott-migrator:<sha> --connection "<migration connection string>"
```

- `DesignTimeDbContextFactory` lets the bundle build the context from a connection string alone; without it the bundle would boot the whole web host and demand JWT/CORS settings just to migrate. It reads `ICEHOTT_MIGRATION_CONNECTION`, then `ConnectionStrings__DefaultConnection`; the bundle's `--connection` overrides both at run time.
- The bundle is idempotent (a second run reports no migrations) and exits non-zero on failure, which stops the pipeline. There is no automatic downgrade.
- Use a migration credential separate from the runtime credential (`STAGING_MIGRATION_CONNECTION` / `PRODUCTION_MIGRATION_CONNECTION` secrets).
- **Expand/contract rule for future migrations:** an application version N must run against schema N and N+1. Add columns/tables/indexes first (nullable or defaulted), deploy code that tolerates both, and remove or tighten in a later release only after no running version depends on the old shape. Data migrations are forward-fixed, never auto-reverted.

## 8. Pipelines

**CI (`ci.yml`)** keeps the original backend/frontend/AI jobs and adds `release-foundation`: deployment-definition validation, shell syntax checks, production image + migrator + AI image builds, and `verify-container.sh`, which proves against the real images: non-root user and revision label, fail-closed start without configuration, migrations apply (count equals the repository's) and re-run as a no-op, bad credentials fail, the same image runs as `Api` and as `Worker` with only their own duties, and `smoke.sh` reports the expected SHA for both.

**Staging (`deploy-staging.yml`)** triggers only from a successful `push` CI run on `main` (or a manual run for a SHA already on `main` with green CI). It resolves the exact tested SHA, refuses commits not reachable from `main`, builds and verifies the images from that SHA, and then:

- **Dry run (default).** Without the repository variable `ICEHOTT_DEPLOY_STAGING=true` nothing is published or deployed, and the summary says so. The migrate, deploy and smoke jobs are skipped rather than pretending to succeed.
- **Enabled.** Publishes `icehott-{api,migrator,ai}:<full-sha>` to GHCR, migrates staging with the bundle, points the Railway services at those images, deploys the web preview, runs the smoke test and records the commit status `deploy/staging`.

**Production (`promote-production.yml`)** is `workflow_dispatch` only, with a required full 40-character `git_sha` and a typed confirmation. `verify` requires: SHA reachable from `main`, CI success, a `deploy/staging` success status for that SHA, and all three published images for that SHA. **No image is rebuilt**; migration and deploy use the staged images. `migrate`, `deploy`, `deploy-web` and `smoke` run in the `production` GitHub Environment, whose required reviewers are the approval gate. Smoke success records `deploy/production`. Mutable tags are rejected everywhere.

`validate_deploy_config.py` enforces these rules mechanically (manual-only production trigger, no `latest`, no secret literals, no untrusted input in shell, staging dry-run gating, production environment on every stage, no image rebuild in promotion, CI gates not removed).

## 9. Rollback

Application rollback is a promotion (or staging deploy) of the previous known-good SHA: same images, same procedure. Because migrations follow expand/contract, the previous application version keeps working on the already-migrated schema. Schema is never rolled back automatically. Recording migration version and timestamp in release metadata, and a demonstrated rollback drill, belong to 7D/7E.

## 10. Manual setup still required

*Nothing below has been done or verified against a live account.*

1. **GitHub:** create Environments `staging` and `production`; add required reviewers to `production`. Set repository variable `ICEHOTT_DEPLOY_STAGING=true` only when ready. Ensure Actions may read/write GHCR packages for this repository (the images carry the `org.opencontainers.image.source` label for linking).
2. **Secrets (per environment):** `STAGING_MIGRATION_CONNECTION` / `PRODUCTION_MIGRATION_CONNECTION`, `RAILWAY_API_TOKEN` (scoped to one Railway environment), `VERCEL_TOKEN`.
3. **Variables:** `RAILWAY_{STAGING,PRODUCTION}_{ENVIRONMENT,API_SERVICE,WORKER_SERVICE,AI_SERVICE}_ID`, `VERCEL_ORG_ID`, `VERCEL_PROJECT_ID`, `STAGING_API_URL`, `PRODUCTION_API_URL`.
4. **Railway:** create separate staging and production environments with PostgreSQL + pgvector and Redis; create services `icehott-api` (public ingress) and `icehott-worker` (`Service__Role=Worker`, no ingress) from the GHCR image, `icehott-ai` (private), and configure GHCR pull credentials. Set runtime variables from section 11. Verify `deploy-railway.sh` against your project first; it targets Railway's GraphQL API and has never run against a live account.
5. **Database:** create a non-superuser application role and a separate migration role; enable the `vector` extension; require TLS.
6. **Vercel:** create the project; set `NEXT_PUBLIC_*` public values only. The web app is the one artifact rebuilt per tier (from the same locked sources at the exact commit); it has no schema coupling.
7. **Domain/TLS:** point the public hostnames at Vercel and the API; set `AllowedHosts` and `Cors__AllowedOrigins__0` to them.

## 11. Required runtime variables (API / Worker)

`Deployment__Tier`, `Service__Role`, `Release__GitSha` (baked into the image), `ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience`, `AiRuntime__BaseUrl` (private service URL), `ArtifactStorage__Provider`, plus `ObjectStorage__Endpoint`/`__Bucket` for a non-local provider; for the API role also `AllowedHosts` and `Cors__AllowedOrigins__0`. Optional: `OpenAiEmbedding__ApiKey`. Never commit values; inject them from Railway variables.

## 12. Known limits and deferred items

- **No object-storage provider yet (7B).** The Local provider is the only implementation, and startup rejects any other provider name. Until 7B ships, a real staging/production deployment can only start with `ArtifactStorage__Provider=Local` and the explicit, temporary `ProductionSafety__AllowLocalArtifactStorage=true` override (data would live on the container volume). This is deliberate: the safe default cannot start rather than silently using disk.
- Database TLS is required by default; a platform-private network without TLS needs the explicit `Database__RequireTransportSecurity=false`.
- Forwarded-header handling behind a TLS-terminating proxy is not configured (`UseHttpsRedirection` is unchanged). Review with the actual platform in 7B/7E.
- `deploy-railway.sh` and the Vercel steps are unverified against live accounts.
- Not started: OpenTelemetry/alerts (7C), backup/restore/DR and rollback drill (7D), load and soak tests and the final release gate (7E), authenticated/tenant-isolation/artifact/workflow staging smoke (the current smoke checks liveness, readiness and release identity).
