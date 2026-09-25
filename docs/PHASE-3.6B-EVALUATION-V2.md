# Phase 3.6B — Expanded Evaluation Dataset v2

Status: deterministic corpus expansion complete; authorized live deterministic + offline semantic promotion gate PASS on 2026-09-25
Dataset: `evals/rag/v2/dataset.json`
Version: `rag-v2`

## Purpose

The v2 corpus expands the four-case v1 regression fixture into a broader synthetic benchmark target before any paid semantic provider is evaluated.

## Coverage

- support/refunds
- administrator MFA and password-only rejection
- severity-one incident reporting
- audit-log retention
- backup retention and restore cadence
- billing and invoice timing
- SAML/SCIM deprovisioning
- workspace export format and link expiry
- account deletion grace period
- API rate limits and throttle behavior
- synthetic EU data-residency fixture
- prompt-injection filtering
- explicit cross-workspace tenant-isolation fixture

The corpus contains 13 documents and 18 evaluation cases. One document belongs to a synthetic foreign workspace and is declared tenant-forbidden for primary-workspace queries.

## Deterministic gate

The integration test validates dataset integrity, source references, category/tag coverage, workspace fixture validity, prompt-injection exclusion, tenant isolation, citations, and the configured retrieval thresholds.

Current thresholds:

- HitRate@K >= 0.95
- Mean Recall@K >= 0.95
- Mean Precision@K >= 0.95
- Citation correctness = 1.00
- Tenant leakage count = 0

## Freeze

`dataset.sha256` locks the exact dataset bytes. Corpus changes require an explicit reviewed hash update and preserve the versioned evaluation history.

## Validation

The local deterministic baseline passes the v2 gate without lowering thresholds.

This dataset does not establish semantic-provider superiority by itself. The authorized 3.6B-7 run on 2026-09-25 passed deterministic and offline semantic promotion gates for the tested OpenAI candidate, with zero tenant leakage and measured provider cost below the approved cap.
