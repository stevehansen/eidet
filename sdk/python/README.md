<div align="center">
  <img src="https://raw.githubusercontent.com/stevehansen/eidet/main/logo-512x512.png" alt="Eidet" width="96" height="96">
</div>

# eidet-sdk

Python client SDK for [Eidet](https://github.com/stevehansen/eidet) — long-term memory for AI coding agents.

## Install

```bash
pip install eidet-sdk
```

## Usage

```python
from eidet_sdk import EidetClient, MemoryType

client = EidetClient()  # defaults to http://localhost:19380

# Store a memory
result = client.store(
    repo="/path/to/project",
    content="The auth module uses JWT with RS256 signing",
    type=MemoryType.OBSERVATION,
    tags=["auth", "jwt"],
)

# Search memories
results = client.recall("/path/to/project", "authentication")

# Get session context (L0 identity + L1 top memories, < 600 tokens)
context = client.context("/path/to/project")

# Browse all memories
page = client.browse("/path/to/project", skip=0, take=50)

# Feedback loop
client.feedback("memories/...", was_used=True)   # echo (useful)
client.feedback("memories/...", was_used=False)  # fizzle (irrelevant)
```

## Context Manager

```python
with EidetClient(url="http://localhost:19380") as client:
    results = client.recall("/path/to/project", "deployment")
```

## API Key Authentication

```python
client = EidetClient(url="http://localhost:19380", api_key="your-api-key")
```

## All Methods

| Method | Description |
|--------|-------------|
| `store(repo, content, ...)` | Store a memory (observation, insight, procedure, heuristic; `negative`/`valence` for dead-ends) |
| `recall(repo, query, **kwargs)` | Search memories by meaning and keywords (filters: type, tags, valence, cross_repo) |
| `context(repo)` | Get compact session context (< 600 tokens) |
| `get_memory(id)` | Get a specific memory by ID |
| `forget(id, reason?)` | Soft-delete a memory |
| `feedback(memory_id, was_used)` | Echo (useful) or fizzle (irrelevant) feedback |
| `history(id)` | Get version chain for a memory |
| `browse(repo, **kwargs)` | Paginated memory listing |
| `graph(repo, limit?)` | Graph data for visualization |
| `repos()` | List all known repositories |
| `intake(repo)` | Ingest project files as seed memories |
| `consolidate(repo)` | Merge related observations into insights |
| `maintenance(repo)` | Run maintenance pipeline |
| `export_markdown(repo)` | Export memories as markdown |
| `health()` | Health check |
| `status()` | Service status and stats |
| `is_available()` | Check if the service is reachable |

## Requirements

- Eidet service running locally (`eidet serve` or installed as system service)
- Python 3.10+
- httpx

## License

[MIT](https://github.com/stevehansen/eidet/blob/main/LICENSE)
