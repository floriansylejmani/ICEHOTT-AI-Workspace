#!/usr/bin/env bash
# Points one Railway service at an immutable, already-built image and redeploys it.
#
#   deploy-railway.sh <image-reference-with-git-sha-tag>
#
# Required environment (supplied by the GitHub Environment, never committed):
#   RAILWAY_API_TOKEN            project token scoped to ONE Railway environment
#   RAILWAY_ENVIRONMENT_ID       target Railway environment (staging or production)
#   RAILWAY_SERVICE_ID           the service to move (icehott-api, icehott-worker, icehott-ai)
#
# STATUS: this adapter has NOT been exercised against a live Railway account (Phase 7A
# does not create platform resources). It uses Railway's public GraphQL API
# (serviceInstanceUpdate + serviceInstanceRedeploy). Verify it against your project once
# the accounts exist, before relying on it. It refuses mutable tags, so a deployment can
# only ever reference an exact release.
set -euo pipefail

image="${1:?usage: deploy-railway.sh <image>:<git-sha>}"
: "${RAILWAY_API_TOKEN:?RAILWAY_API_TOKEN is required}"
: "${RAILWAY_ENVIRONMENT_ID:?RAILWAY_ENVIRONMENT_ID is required}"
: "${RAILWAY_SERVICE_ID:?RAILWAY_SERVICE_ID is required}"

if ! [[ "$image" =~ ^[a-z0-9./_-]+:[0-9a-f]{40}$ ]]; then
  echo "deploy-railway: image must be pinned to a full 40-character Git SHA tag (no 'latest')." >&2
  exit 2
fi
if ! [[ "$RAILWAY_ENVIRONMENT_ID$RAILWAY_SERVICE_ID" =~ ^[A-Za-z0-9-]+$ ]]; then
  echo "deploy-railway: environment/service ids contain unexpected characters." >&2
  exit 2
fi

endpoint="${RAILWAY_GRAPHQL_ENDPOINT:-https://backboard.railway.com/graphql/v2}"

call() {
  local query="$1" response
  response="$(curl -sS --max-time 60 -X POST "$endpoint" \
    -H "Authorization: Bearer ${RAILWAY_API_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data "$query")"
  if grep -q '"errors"' <<<"$response"; then
    echo "deploy-railway: Railway API returned errors (response withheld)." >&2
    exit 1
  fi
}

call "{\"query\":\"mutation { serviceInstanceUpdate(serviceId: \\\"${RAILWAY_SERVICE_ID}\\\", environmentId: \\\"${RAILWAY_ENVIRONMENT_ID}\\\", input: { source: { image: \\\"${image}\\\" } }) }\"}"
call "{\"query\":\"mutation { serviceInstanceRedeploy(serviceId: \\\"${RAILWAY_SERVICE_ID}\\\", environmentId: \\\"${RAILWAY_ENVIRONMENT_ID}\\\") }\"}"

echo "deploy-railway: requested ${image} for the configured service."
