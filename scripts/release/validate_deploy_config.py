#!/usr/bin/env python3
"""Static validation of ICEHOTT deployment definitions (Phase 7A).

Runs in CI and locally with only PyYAML. It parses every workflow and enforces the
release-safety rules that must never regress. It is deliberately strict about things a
reviewer could miss: mutable image tags, secrets in YAML, untrusted input interpolated
into shell, production workflows that trigger automatically, and weakened CI gates.

Usage: python scripts/release/validate_deploy_config.py [repo-root]
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

import yaml

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"

errors: list[str] = []


def fail(message: str) -> None:
    errors.append(message)


def triggers(doc: dict) -> dict:
    # PyYAML (YAML 1.1) parses the bare key `on` as boolean True.
    value = doc.get("on", doc.get(True))
    if isinstance(value, str):
        return {value: None}
    if isinstance(value, list):
        return {name: None for name in value}
    return value or {}


def load(name: str) -> tuple[dict, str]:
    path = WORKFLOWS / name
    if not path.exists():
        fail(f"{name}: workflow file is missing")
        return {}, ""
    text = path.read_text(encoding="utf-8")
    try:
        doc = yaml.safe_load(text)
    except yaml.YAMLError as exc:  # pragma: no cover - message only
        fail(f"{name}: invalid YAML ({exc.__class__.__name__})")
        return {}, text
    if not isinstance(doc, dict) or "jobs" not in doc:
        fail(f"{name}: not a workflow (no jobs)")
        return {}, text
    return doc, text


def run_scripts(doc: dict):
    for job_name, job in doc.get("jobs", {}).items():
        for step in job.get("steps", []) or []:
            if "run" in step:
                yield job_name, step.get("name", "(unnamed)"), step["run"]


UNTRUSTED = re.compile(
    r"\$\{\{\s*(github\.event\.|github\.head_ref|inputs\.|github\.ref_name)", re.IGNORECASE
)
SECRET_SHAPES = [
    re.compile(r"ghp_[A-Za-z0-9]{20,}"),
    re.compile(r"github_pat_[A-Za-z0-9_]{20,}"),
    re.compile(r"AKIA[0-9A-Z]{16}"),
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
    re.compile(r"xox[baprs]-[A-Za-z0-9-]{10,}"),
    re.compile(r"sk-[A-Za-z0-9]{32,}"),
]
MUTABLE_TAG = re.compile(r"(:|@)latest\b", re.IGNORECASE)


def check_common(name: str, doc: dict, text: str) -> None:
    for pattern in SECRET_SHAPES:
        if pattern.search(text):
            fail(f"{name}: contains a secret-shaped literal")
    if MUTABLE_TAG.search(text):
        fail(f"{name}: references a mutable 'latest' tag")
    for job, step, script in run_scripts(doc):
        if UNTRUSTED.search(script):
            fail(f"{name}: job '{job}' step '{step}' interpolates untrusted input into a shell script; pass it via env")
        if re.search(r"echo[^\n]*\$\{\{\s*secrets\.", script):
            fail(f"{name}: job '{job}' step '{step}' echoes a secret")
        if re.search(r"set\s+-x|bash\s+-x", script):
            fail(f"{name}: job '{job}' step '{step}' enables shell tracing (would print secrets)")
    permissions = doc.get("permissions")
    if not isinstance(permissions, dict) or permissions.get("contents") != "read":
        fail(f"{name}: top-level permissions must default to contents: read")
    if permissions and any(v == "write" for v in permissions.values()):
        fail(f"{name}: top-level permissions must not grant write; scope it per job")


def check_ci() -> None:
    doc, text = load("ci.yml")
    if not doc:
        return
    check_common("ci.yml", doc, text)
    jobs = doc["jobs"]
    for required in ("backend", "frontend", "ai", "release-foundation"):
        if required not in jobs:
            fail(f"ci.yml: required job '{required}' is missing (CI gates must not be removed)")
    t = triggers(doc)
    if "pull_request" not in t or "push" not in t:
        fail("ci.yml: must run on push and pull_request")
    backend_text = yaml.safe_dump(jobs.get("backend", {}), width=100000)
    for needle in ("dotnet test", "dotnet build", "migrations script", "database update"):
        if needle not in backend_text:
            fail(f"ci.yml: backend job no longer runs '{needle}'")
    release_text = yaml.safe_dump(jobs.get("release-foundation", {}), width=100000)
    for needle in ("validate_deploy_config.py", "verify-container.sh", "--target migrator"):
        if needle not in release_text:
            fail(f"ci.yml: release-foundation job must run '{needle}'")


def check_staging() -> None:
    doc, text = load("deploy-staging.yml")
    if not doc:
        return
    check_common("deploy-staging.yml", doc, text)
    t = triggers(doc)
    run_trigger = t.get("workflow_run") or {}
    if "CI" not in (run_trigger.get("workflows") or []) or run_trigger.get("branches") != ["main"]:
        fail("deploy-staging.yml: must trigger from a completed CI run on main only")
    if "push" in t or "pull_request" in t or "schedule" in t:
        fail("deploy-staging.yml: must not trigger directly on push, pull_request or schedule")
    jobs = doc["jobs"]
    for required in ("resolve", "build", "migrate", "deploy", "deploy-web", "smoke"):
        if required not in jobs:
            fail(f"deploy-staging.yml: job '{required}' is missing")
    for gated in ("migrate", "deploy", "deploy-web", "smoke"):
        job = jobs.get(gated, {})
        if "deploy_enabled" not in str(job.get("if", "")):
            fail(f"deploy-staging.yml: '{gated}' must be gated on deploy_enabled (dry-run safety)")
        if job.get("environment") != "staging":
            fail(f"deploy-staging.yml: '{gated}' must use the 'staging' environment")
    if "workflow_run.head_sha" not in text:
        fail("deploy-staging.yml: must deploy the exact tested workflow_run head_sha")
    if "merge-base --is-ancestor" not in text:
        fail("deploy-staging.yml: must verify the commit is reachable from main")
    for command in ("deploy-railway.sh",):
        if command not in text:
            fail(f"deploy-staging.yml: does not use {command}")
    if "environment: production" in text:
        fail("deploy-staging.yml: must never reference the production environment")


def check_production() -> None:
    doc, text = load("promote-production.yml")
    if not doc:
        return
    check_common("promote-production.yml", doc, text)
    t = triggers(doc)
    if set(t) != {"workflow_dispatch"}:
        fail("promote-production.yml: must be manual (workflow_dispatch) only")
    inputs = (t.get("workflow_dispatch") or {}).get("inputs", {})
    if not inputs.get("git_sha", {}).get("required"):
        fail("promote-production.yml: git_sha input must be required")
    if not inputs.get("confirm", {}).get("required"):
        fail("promote-production.yml: confirm input must be required")
    jobs = doc["jobs"]
    for required in ("verify", "migrate", "deploy", "deploy-web", "smoke"):
        if required not in jobs:
            fail(f"promote-production.yml: job '{required}' is missing")
    for guarded in ("migrate", "deploy", "deploy-web", "smoke"):
        if jobs.get(guarded, {}).get("environment") != "production":
            fail(f"promote-production.yml: '{guarded}' must use the 'production' environment (approval gate)")
    if "verify" not in str(jobs.get("migrate", {}).get("needs")):
        fail("promote-production.yml: migrate must depend on verify")
    if "migrate" not in str(jobs.get("deploy", {}).get("needs")):
        fail("promote-production.yml: deploy must depend on the migration stage")
    if "deploy" not in str(jobs.get("smoke", {}).get("needs")):
        fail("promote-production.yml: smoke must depend on deploy")
    if "docker build" in text:
        fail("promote-production.yml: must not rebuild images; it promotes the staged ones")
    for needle in ("deploy/staging", "docker manifest inspect", "merge-base --is-ancestor"):
        if needle not in text:
            fail(f"promote-production.yml: verification is missing '{needle}'")
    if "^[0-9a-f]{40}$" not in text:
        fail("promote-production.yml: must require a full 40-character SHA")


def check_repository() -> None:
    dockerfile = (ROOT / "backend" / "Dockerfile").read_text(encoding="utf-8")
    if not re.search(r"^USER\s+\$APP_UID\s*$", dockerfile, re.MULTILINE):
        fail("backend/Dockerfile: runtime must drop to $APP_UID")
    if re.search(r"^USER\s+(root|0)\b", dockerfile, re.MULTILINE):
        fail("backend/Dockerfile: must not run as root")
    if MUTABLE_TAG.search(dockerfile):
        fail("backend/Dockerfile: references a mutable 'latest' tag")
    if not re.search(r"^ARG GIT_SHA", dockerfile, re.MULTILINE):
        fail("backend/Dockerfile: GIT_SHA build argument is required")
    if re.search(r"COPY\s+[^\n]*\.env", dockerfile):
        fail("backend/Dockerfile: copies an .env file into the image")
    if "AS migrator" not in dockerfile:
        fail("backend/Dockerfile: migrator target is missing")

    production = (ROOT / "backend" / "src" / "ICEHOTT.API" / "appsettings.Production.json").read_text(encoding="utf-8")
    for needle in ('"AutoMigrate": false', '"RequireTransportSecurity": true'):
        if needle not in production:
            fail(f"appsettings.Production.json: expected {needle}")

    ignore = (ROOT / ".dockerignore").read_text(encoding="utf-8").splitlines()
    for entry in (".env", ".git"):
        if entry not in ignore:
            fail(f".dockerignore: '{entry}' must be excluded from the build context")

    for script in ("smoke.sh", "verify-container.sh", "deploy-railway.sh"):
        if not (ROOT / "scripts" / "release" / script).exists():
            fail(f"scripts/release/{script}: missing")


for check in (check_ci, check_staging, check_production, check_repository):
    check()

if errors:
    print("deployment configuration validation FAILED:")
    for error in errors:
        print(f"  - {error}")
    sys.exit(1)

print("deployment configuration validation passed")
