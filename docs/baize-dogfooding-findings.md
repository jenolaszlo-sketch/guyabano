# Baize dogfooding findings

Issues discovered while using Penghou.Baize from Guyabano should be fixed at the lowest appropriate layer. Guyabano may define workflow-level policy, but it should not duplicate provider protocol handling.

## Transient provider-capacity failures

Observed from Gemini during a streaming request:

```text
HTTP 503
status: UNAVAILABLE
message: This model is currently experiencing high demand.
```

### Baize responsibility

- Represent provider HTTP failures with a typed exception or result containing the status code, provider error code and message, endpoint, model, request identifier, and `Retry-After` value when available.
- Classify `408`, `429`, `502`, `503`, and `504` as transient by default while allowing providers to refine the classification.
- Apply a small, bounded endpoint retry policy with exponential backoff and jitter.
- Preserve the original request when retrying a capacity failure.
- After endpoint retries are exhausted, allow the router to try the next compatible configured endpoint or model.
- Record every attempt, delay, terminal outcome, and fallback in diagnostics, logs, and telemetry.
- Surface a structured transient-exhaustion failure when recovery is unsuccessful.

### Guyabano responsibility

- Use Zhinu durable steps for longer-lived workflow retries after Baize's short transport retry budget is exhausted.
- Report a provider-busy/retrying state to the UI.
- Decide whether the workflow permits model escalation.
- Avoid interpreting Gemini-specific error bodies or duplicating HTTP retry rules.

### Retry boundary

- Provider `503`: retry or route fallback with the same prompt.
- Invalid JSON or schema: retry only the failed planning node with repair diagnostics.
- Architecture defect: review or amend the architecture artifact.
- Product ambiguity: request user clarification.

Keep Baize endpoint retries deliberately small to avoid multiplying them with Zhinu step retries.
