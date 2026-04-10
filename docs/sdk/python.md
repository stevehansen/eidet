---
title: Python
parent: Client SDKs
nav_order: 2
---

# Python SDK

`eidet-sdk` — Python client using `httpx`. Python 3.10+.

## Installation

```bash
pip install eidet-sdk
```

## Quick Start

```python
from eidet_sdk import EidetClient, MemoryType

with EidetClient("http://localhost:19380") as eidet:
    # Store a memory
    result = eidet.store(
        repo="/path/to/project",
        content="The auth module uses JWT with RS256 signing",
        type=MemoryType.OBSERVATION,
        tags=["auth", "jwt"],
        importance=0.7,
    )
    print(f"Stored: {result['id']}")

    # Recall memories
    memories = eidet.recall(
        repo="/path/to/project",
        query="authentication",
        limit=5,
        type=MemoryType.INSIGHT,
    )
    for m in memories:
        print(f"[{m['type']}] {m.get('oneLiner', m['content'])}")

    # Get session context
    context = eidet.context("/path/to/project")
    print(context)
```

## Authentication

```python
eidet = EidetClient("http://localhost:19380", api_key="eidet_abc123...")
```

## API Reference

### Constructor

```python
EidetClient(url: str = "http://localhost:19380", api_key: str | None = None)
```

The client is a context manager:

```python
with EidetClient() as eidet:
    ...

# Or manage lifecycle manually
eidet = EidetClient()
try:
    ...
finally:
    eidet.close()
```

### Core Methods

```python
# Store a memory
eidet.store(
    repo: str,
    content: str,
    type: MemoryType | str = MemoryType.OBSERVATION,
    *,
    tags: list[str] | None = None,
    importance: float = 0.5,
    source: str = "sdk",
    session_id: str | None = None,
    supersedes: str | None = None,
) -> dict[str, Any]

# Search memories
eidet.recall(
    repo: str,
    query: str,
    *,
    limit: int = 10,
    type: MemoryType | str | None = None,
    tags: list[str] | None = None,
) -> list[dict[str, Any]]

# Get L0+L1 context
eidet.context(repo: str) -> str

# Get a single memory
eidet.get_memory(memory_id: str) -> dict[str, Any]

# Soft-delete
eidet.forget(memory_id: str, reason: str | None = None) -> bool

# Report feedback
eidet.feedback(memory_id: str, was_used: bool) -> bool

# Version chain
eidet.history(memory_id: str) -> list[dict[str, Any]]
```

### Browse & Graph

```python
# Paginated browse
eidet.browse(repo: str, *, skip: int = 0, take: int = 50,
             type: MemoryType | str | None = None) -> dict[str, Any]

# Knowledge graph data
eidet.graph(repo: str, limit: int = 200) -> dict[str, Any]

# List all repos
eidet.repos() -> list[str]
```

### Operations

```python
# Ingest project files
eidet.intake(repo: str) -> dict[str, Any]

# Run consolidation
eidet.consolidate(repo: str) -> dict[str, Any]

# Run maintenance
eidet.maintenance(repo: str) -> dict[str, Any]

# Export as markdown
eidet.export_markdown(repo: str) -> str
```

### Health

```python
eidet.health() -> dict[str, Any]
eidet.status() -> dict[str, Any]
```

### Error Handling

```python
from eidet_sdk import EidetClient, EidetError

try:
    eidet.store(...)
except EidetError as e:
    print(f"HTTP {e.status}: {e.body}")
```

## Types

### MemoryType Enum

```python
from eidet_sdk import MemoryType

MemoryType.OBSERVATION  # "observation"
MemoryType.INSIGHT      # "insight"
MemoryType.PROCEDURE    # "procedure"
MemoryType.HEURISTIC    # "heuristic"
```

### StoreRequest Dataclass

```python
from eidet_sdk import StoreRequest

req = StoreRequest(
    repo="/path/to/project",
    content="...",
    type=MemoryType.OBSERVATION,
    tags=["tag1", "tag2"],
    importance=0.7,
)
```

## Examples

### CI/CD: Store Build Observations

```python
from eidet_sdk import EidetClient, MemoryType

with EidetClient() as eidet:
    # After a flaky test
    eidet.store(
        repo="/path/to/project",
        content="Test test_user_auth flakes on Windows when path > 260 chars",
        type=MemoryType.OBSERVATION,
        tags=["ci", "flaky-test", "windows"],
        importance=0.8,
        source="ci-pipeline",
    )
```

### Batch Export

```python
from eidet_sdk import EidetClient

with EidetClient() as eidet:
    repos = eidet.repos()
    for repo in repos:
        md = eidet.export_markdown(repo)
        with open(f"{repo.replace('/', '_')}_memories.md", "w") as f:
            f.write(md)
```
