#!/usr/bin/env bash
# Points one Railway service at an immutable, already-built image and redeploys it.
#
#   deploy-railway.sh <image-reference-with-full-git-sha-tag>
#
# Required environment (supplied by the GitHub Environment, never committed):
#   RAILWAY_API_TOKEN        project token scoped to ONE Railway environment
#   RAILWAY_ENVIRONMENT_ID   target Railway environment (staging or production)
#   RAILWAY_SERVICE_ID       the service to move (icehott-api, icehott-worker, icehott-ai)
# Optional:
#   RAILWAY_TOKEN_KIND       "project" (default, Project-Access-Token header) or "bearer"
#                            (account/workspace token, Authorization: Bearer)
#
# LIVE PROVIDER VERIFICATION REQUIRED. This adapter has never run against a Railway account.
#   Statically confirmed against docs.railway.com/integrations/api:
#     - endpoint https://backboard.railway.com/graphql/v2
#     - project tokens authenticate with the Project-Access-Token header (NOT Bearer)
#   NOT confirmed (needs schema introspection with a real token):
#     - the mutations serviceInstanceUpdate(input.source.image) and serviceInstanceRedeploy
#       and their argument/return shapes. The script fails closed if either does not answer
#       with `true`, but the names themselves may need correcting.
# It refuses mutable tags, so a deployment can only ever reference an exact release.
set -euo pipefail

image="${1:?usage: deploy-railway.sh <image>:<full-git-sha>}"
: "${RAILWAY_API_TOKEN:?RAILWAY_API_TOKEN is required}"
: "${RAILWAY_ENVIRONMENT_ID:?RAILWAY_ENVIRONMENT_ID is required}"
: "${RAILWAY_SERVICE_ID:?RAILWAY_SERVICE_ID is required}"

if ! [[ "$image" =~ ^[a-z0-9./_-]+:[0-9a-f]{40}$ ]]; then
  echo "deploy-railway: image must be pinned to a full 40-character Git SHA tag (no 'latest')." >&2
  exit 2
fi
if ! [[ "$RAILWAY_ENVIRONMENT_ID" =~ ^[A-Za-z0-9-]+$ && "$RAILWAY_SERVICE_ID" =~ ^[A-Za-z0-9-]+$ ]]; then
  echo "deploy-railway: environment/service ids contain unexpected characters." >&2
  exit 2
fi

case "${RAILWAY_TOKEN_KIND:-project}" in
  project) auth_header="Project-Access-Token: ${RAILWAY_API_TOKEN}" ;;
  bearer) auth_header="Authorization: Bearer ${RAILWAY_API_TOKEN}" ;;
  *) echo "deploy-railway: RAILWAY_TOKEN_KIND must be 'project' or 'bearer'." >&2; exit 2 ;;
esac

endpoint="${RAILWAY_GRAPHQL_ENDPOINT:-https://backboard.railway.com/graphql/v2}"

# call <mutation-name> <graphql-document>; requires data.<mutation-name> to be exactly true.
call() {
  local name="$1" query="$2" response
  # --fail turns HTTP 4xx/5xx into a non-zero exit; the response body is never printed
  # because it can echo request details.
  if ! response="$(curl -sS --fail --max-time 60 -X POST "$endpoint" \
    -H "$auth_header" -H 'Content-Type: application/json' --data "$query" 2>/dev/null)"; then
    echo "deploy-railway: request for $name failed (HTTP error or network failure)." >&2
    exit 1
  fi
  if grep -q '"errors"' <<<"$response"; then
    echo "deploy-railway: Railway API returned errors for $name (response withheld)." >&2
    exit 1
  fi
  if ! grep -Eq "\"$name\" *: *true" <<<"$response"; then
    echo "deploy-railway: $name did not confirm success (response withheld)." >&2
    exit 1
  fi
}

call serviceInstanceUpdate "{\"query\":\"mutation { serviceInstanceUpdate(serviceId: \\\"${RAILWAY_SERVICE_ID}\\\", environmentId: \\\"${RAILWAY_ENVIRONMENT_ID}\\\", input: { source: { image: \\\"${image}\\\" } }) }\"}"
call serviceInstanceRedeploy "{\"query\":\"mutation { serviceInstanceRedeploy(serviceId: \\\"${RAILWAY_SERVICE_ID}\\\", environmentId: \\\"${RAILWAY_ENVIRONMENT_ID}\\\") }\"}"

echo "deploy-railway: requested ${image} for the configured service (rollout is verified by the smoke stage)."
