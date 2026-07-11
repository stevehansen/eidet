---
title: Hooks
nav_order: 8
---

# Hooks

Eidet supports lifecycle hooks — custom commands that run at key points in the memory lifecycle. Inspired by [Claude Code hooks](https://docs.anthropic.com/en/docs/claude-code/hooks).

## Hook Events

| Event | When | Can reject? | Use cases |
|-------|------|-------------|-----------|
| `preStore` | Before a memory is written | Yes | Custom validation, content filtering, approval gates |
| `postStore` | After a memory is stored | No | Notifications, logging, webhooks, sync triggers |
| `preRecall` | Before a search executes | Yes | Query rewriting, access control |
| `postRecall` | After results are returned | No | Audit logging, analytics |
| `preForget` | Before a memory is deleted | Yes | Deletion approval, archival |
| `postForget` | After a memory is deleted | No | Cleanup, notifications |

## Configuration

Add hooks to your Eidet config file:

```json
{
  "hooks": {
    "preStore": [
      {
        "command": "python scripts/validate-memory.py",
        "timeoutSeconds": 10,
        "enabled": true
      }
    ],
    "postStore": [
      {
        "command": "node scripts/notify-slack.js",
        "timeoutSeconds": 5,
        "enabled": true
      }
    ]
  }
}
```

Config file location:
- **Windows**: `%APPDATA%\Eidet\eidet.json`
- **macOS/Linux**: `~/.eidet/eidet.json`

## How Hooks Work

### Execution

1. Eidet serializes the event context as JSON
2. The hook command is launched as a child process
3. Context JSON is written to the process's **stdin**
4. Eidet waits for the process to exit (up to `timeoutSeconds`)

### Pre-hooks (gating)

Pre-hooks can **reject** the operation:

- **Exit code 0** — operation proceeds
- **Exit code non-zero** — operation is rejected. The rejection reason is taken from stderr (or stdout if stderr is empty)
- **Timeout** — treated as rejection

Multiple pre-hooks run sequentially. If any hook rejects, the operation stops immediately.

### Post-hooks (notification)

Post-hooks are **fire-and-forget**:

- Exit code is ignored
- Errors are swallowed (won't affect the operation)
- Still run sequentially but don't block the response

## Context Format

Every hook receives a JSON object on stdin:

```json
{
  "event": "pre-store",
  "repo": "P--MyProject",
  "data": { ... },
  "timestamp": "2026-04-10T14:30:00.000Z"
}
```

### pre-store / post-store

```json
{
  "event": "pre-store",
  "repo": "P--MyProject",
  "data": {
    "id": "memories/P--MyProject/observation/abc123",
    "content": "The auth module uses JWT with RS256",
    "type": "observation",
    "tags": ["auth", "jwt"],
    "importance": 0.7,
    "source": "claude-session"
  }
}
```

Pre-store and post-store receive the same payload, including the deterministic `id` the entry will be stored under.

### pre-recall / post-recall

```json
{
  "event": "pre-recall",
  "repo": "P--MyProject",
  "data": {
    "query": "how does authentication work",
    "limit": 10,
    "type": "insight",
    "tags": []
  }
}
```

Post-recall includes `resultCount`.

### pre-forget / post-forget

```json
{
  "event": "pre-forget",
  "repo": "",
  "data": {
    "id": "memories/P--MyProject/observation/abc123",
    "reason": "outdated information"
  }
}
```

## Examples

### Validation Gate (Python)

Block memories that mention specific terms:

```python
#!/usr/bin/env python3
# scripts/validate-memory.py
import json, sys

context = json.load(sys.stdin)
content = context["data"].get("content", "")

blocked_terms = ["TODO", "FIXME", "HACK"]
for term in blocked_terms:
    if term in content:
        print(f"Memory contains blocked term: {term}", file=sys.stderr)
        sys.exit(1)

sys.exit(0)
```

### Slack Notification (Node.js)

Post to Slack when a high-importance memory is stored:

```javascript
#!/usr/bin/env node
// scripts/notify-slack.js
const context = JSON.parse(require('fs').readFileSync('/dev/stdin', 'utf8'));

if (context.data.importance >= 0.8) {
  fetch(process.env.SLACK_WEBHOOK_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      text: `New high-importance memory in ${context.repo}: ${context.data.content.slice(0, 200)}`
    })
  });
}
```

### Audit Logger (Bash)

Log all memory operations to a file:

```bash
#!/bin/bash
# scripts/audit-log.sh
cat /dev/stdin | jq -c '{event: .event, repo: .repo, ts: .timestamp}' \
  >> ~/.eidet/audit.jsonl
```

### Deletion Approval Gate

Prevent deletion of high-importance memories:

```python
#!/usr/bin/env python3
# scripts/protect-important.py
import json, sys, urllib.request

context = json.load(sys.stdin)
memory_id = context["data"]["id"]

# Fetch the memory to check importance
res = urllib.request.urlopen(f"http://localhost:19380/api/eidet/{memory_id}")
memory = json.loads(res.read())

if memory.get("importance", 0) >= 0.9:
    print("Cannot delete memories with importance >= 0.9", file=sys.stderr)
    sys.exit(1)
```

## Checking Hook Status

```bash
# See hook counts per event
eidet config list | grep hooks
```

Output:
```
hooks.preStore    1 hook(s)
hooks.postStore   1 hook(s)
hooks.preRecall   0 hook(s)
hooks.postRecall  0 hook(s)
hooks.preForget   0 hook(s)
hooks.postForget  0 hook(s)
```

## Performance

- When **no hooks are configured**, the runner short-circuits before any process spawn — zero overhead
- Pre-hooks add latency (process spawn + execution time). Keep them fast.
- Post-hooks run after the response is sent — they don't slow down the caller
- Use `enabled: false` to temporarily disable a hook without removing it
