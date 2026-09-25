# ICEHOTT RAG Evaluation Dataset v2

This is the expanded synthetic, non-private corpus used by the Phase 3.6B provider benchmark gate.

## Goals

- broaden deterministic retrieval coverage beyond the four-case v1 baseline;
- cover support, security, governance, reliability, billing, identity, data management, API, residency, safety, and tenant isolation;
- keep every fixture synthetic and safe to commit;
- make provider comparisons reproducible against a frozen dataset version.

## Safety and isolation

The corpus includes one prompt-injection fixture that must never reach retrieved context or citations.
It also includes a synthetic foreign-workspace document. Queries run in the primary workspace and must never retrieve or cite that foreign source.

## Promotion use

Deterministic thresholds are necessary but not sufficient for provider promotion.
The live provider gate must additionally record offline semantic quality, latency, cost, provider errors, privacy configuration, and outage behavior.

## Freeze

The exact dataset bytes are locked by `dataset.sha256`.
Changing the corpus requires a new reviewed hash and must preserve the versioned evaluation history.
