# ICEHOTT Security Baseline

## Principles
- Least privilege
- Explicit tenant boundaries
- Human approval for sensitive actions
- No production secrets in source control
- Auditable agent and tool execution

## Required controls
- Authentication and session hardening
- Workspace RBAC
- Rate limiting
- Input and file validation
- Prompt-injection defenses for retrieved content
- Tool allowlists and per-tool permissions
- Secret storage through deployment platforms
- Structured audit logs
- Security headers and HTTPS in production

## AI-specific controls
- Separate model output from executable tool arguments
- Validate tool schemas server-side
- Do not trust retrieved documents as instructions
- Require approval for email sending, destructive writes, and production mutations
- Record model, prompt version, tool calls, and final outcome for each run
