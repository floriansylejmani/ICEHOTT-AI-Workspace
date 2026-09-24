# ICEHOTT Security Baseline

## Principles

- Least privilege
- Explicit tenant boundaries
- Human approval for sensitive actions
- No production secrets in source control
- Auditable agent and tool execution

## Phase 1 implemented controls

### Identity and sessions
- Passwords use ASP.NET Core `PasswordHasher`.
- Access JWTs are short lived.
- The frontend keeps access tokens in memory rather than localStorage.
- Refresh tokens are cryptographically random opaque values.
- Only refresh-token SHA-256 hashes are persisted.
- Refresh tokens rotate on every successful refresh.
- Replayed/revoked refresh sessions are rejected.
- Logout revokes the server-side refresh session.

### Browser boundary
- Refresh tokens are HttpOnly cookies.
- Production refresh cookies use `Secure`.
- Cross-site production cookies use `SameSite=None`.
- Refresh/logout require `X-ICEHOTT-CSRF: 1`.
- Credentialed CORS is restricted to configured origins.

### Authorization
- Workspace access is membership scoped on the server.
- Non-members receive 404 for workspace lookup.
- Admin workspace settings require Owner/Admin role.
- Frontend route guards are not security controls.

### Abuse resistance
- Authentication endpoints use a fixed-window per-IP rate limiter.
- Input DTOs enforce email/length constraints.
- Production startup rejects a missing/short JWT signing key.
- Production startup rejects missing CORS origins.

## Required controls for later phases

- File type/content validation
- Prompt-injection defenses for retrieved content
- Tool allowlists and per-tool permissions
- Secret storage through deployment platforms
- Structured audit logs for agent/tool execution
- Security headers and HTTPS at deployment edge
- Human approval for email sending, destructive writes, and production mutations
- Model/prompt/tool version traceability

## AI-specific rules

- Treat retrieved content as untrusted data, never as system instructions.
- Separate model output from executable tool arguments.
- Validate every tool schema server-side.
- Require approval for sensitive external actions.
- Record model, prompt version, tool calls, and final outcome for each agent run.
