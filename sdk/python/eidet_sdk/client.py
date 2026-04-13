"""Eidet Python SDK — REST API client for Eidet memory service."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any

import httpx


class MemoryType(str, Enum):
    OBSERVATION = "observation"
    INSIGHT = "insight"
    PROCEDURE = "procedure"
    HEURISTIC = "heuristic"


@dataclass
class StoreRequest:
    repo: str
    content: str
    type: MemoryType = MemoryType.OBSERVATION
    tags: list[str] = field(default_factory=list)
    importance: float = 0.5
    source: str = "sdk"
    session_id: str | None = None
    supersedes: str | None = None


class EidetError(Exception):
    def __init__(self, status: int, body: str) -> None:
        self.status = status
        self.body = body
        super().__init__(f"Eidet API error {status}: {body}")


class EidetClient:
    """Client for the Eidet memory service REST API."""

    def __init__(self, url: str = "http://localhost:19380", api_key: str | None = None) -> None:
        self._base = url.rstrip("/")
        headers: dict[str, str] = {}
        if api_key:
            headers["Authorization"] = f"Bearer {api_key}"
        self._client = httpx.Client(base_url=self._base, headers=headers, timeout=30.0)

    def close(self) -> None:
        self._client.close()

    def __enter__(self) -> EidetClient:
        return self

    def __exit__(self, *args: Any) -> None:
        self.close()

    # ─── Core operations ─────────────────────────────────────────

    def store(
        self,
        repo: str,
        content: str,
        type: MemoryType | str = MemoryType.OBSERVATION,
        *,
        tags: list[str] | None = None,
        importance: float = 0.5,
        source: str = "sdk",
        session_id: str | None = None,
        supersedes: str | None = None,
    ) -> dict[str, Any]:
        body: dict[str, Any] = {
            "repo": repo,
            "content": content,
            "type": str(type.value) if isinstance(type, MemoryType) else type,
            "importance": importance,
            "source": source,
        }
        if tags:
            body["tags"] = tags
        if session_id:
            body["sessionId"] = session_id
        if supersedes:
            body["supersedes"] = supersedes
        return self._post("/api/eidet", body)

    def recall(
        self,
        repo: str,
        query: str,
        *,
        limit: int = 10,
        type: MemoryType | str | None = None,
        tags: list[str] | None = None,
    ) -> list[dict[str, Any]]:
        params: dict[str, str] = {"repo": repo, "q": query, "limit": str(limit)}
        if type:
            params["type"] = str(type.value) if isinstance(type, MemoryType) else type
        if tags:
            params["tags"] = ",".join(tags)
        data = self._get("/api/eidet/search", params=params)
        return data["results"]

    def context(self, repo: str) -> str:
        data = self._get("/api/eidet/context", params={"repo": repo})
        return data["context"]

    def get_memory(self, memory_id: str) -> dict[str, Any]:
        return self._get(f"/api/eidet/{memory_id}")

    def forget(self, memory_id: str, reason: str | None = None) -> bool:
        params = {"reason": reason} if reason else {}
        data = self._delete(f"/api/eidet/{memory_id}", params=params)
        return data.get("forgotten", False)

    def feedback(self, memory_id: str, was_used: bool) -> bool:
        data = self._post("/api/eidet/feedback", {"memoryId": memory_id, "wasUsed": was_used})
        return data.get("applied", False)

    def history(self, memory_id: str) -> list[dict[str, Any]]:
        data = self._get(f"/api/eidet/history/{memory_id}")
        return data["chain"]

    # ─── Browse & Graph ──────────────────────────────────────────

    def browse(
        self,
        repo: str,
        *,
        skip: int = 0,
        take: int = 50,
        type: MemoryType | str | None = None,
    ) -> dict[str, Any]:
        params: dict[str, str] = {"repo": repo, "skip": str(skip), "take": str(take)}
        if type:
            params["type"] = str(type.value) if isinstance(type, MemoryType) else type
        return self._get("/api/eidet/browse", params=params)

    def graph(self, repo: str, limit: int = 200) -> dict[str, Any]:
        return self._get("/api/eidet/graph", params={"repo": repo, "limit": str(limit)})

    def repos(self) -> list[str]:
        data = self._get("/api/eidet/repos")
        return [r["repoId"] for r in data["repos"]]

    # ─── Operations ──────────────────────────────────────────────

    def intake(self, repo: str) -> dict[str, Any]:
        return self._post(f"/api/eidet/intake?repo={repo}")

    def consolidate(self, repo: str) -> dict[str, Any]:
        return self._post(f"/api/eidet/consolidate?repo={repo}")

    def maintenance(self, repo: str) -> dict[str, Any]:
        return self._post(f"/api/maintenance?repo={repo}")

    def export_markdown(self, repo: str) -> str:
        res = self._client.get("/api/eidet/export", params={"repo": repo})
        res.raise_for_status()
        return res.text

    # ─── Usage & Context ───────────────────────────────────────

    def usage(self, repo: str, days: int = 30) -> dict[str, Any]:
        return self._get("/api/eidet/usage", params={"repo": repo, "days": str(days)})

    def usage_timeseries(
        self, repo: str, operation: str, days: int = 30
    ) -> list[dict[str, Any]]:
        data = self._get(
            "/api/eidet/usage/timeseries",
            params={"repo": repo, "operation": operation, "days": str(days)},
        )
        return data["data"]

    def usage_hourly(self, repo: str, days: int = 7) -> list[dict[str, Any]]:
        data = self._get("/api/eidet/usage/hourly", params={"repo": repo, "days": str(days)})
        return data["buckets"]

    def context_preview(self, repo: str, tokens: int = 600) -> dict[str, Any]:
        return self._get(
            "/api/eidet/context/preview",
            params={"repo": repo, "tokens": str(tokens)},
        )

    # ─── Health ──────────────────────────────────────────────────

    def health(self) -> dict[str, Any]:
        return self._get("/api/health")

    def status(self) -> dict[str, Any]:
        return self._get("/api/status")

    # ─── HTTP helpers ────────────────────────────────────────────

    def _get(self, path: str, params: dict[str, str] | None = None) -> dict[str, Any]:
        res = self._client.get(path, params=params)
        if not res.is_success:
            raise EidetError(res.status_code, res.text)
        return res.json()

    def _post(self, path: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
        res = self._client.post(path, json=body)
        if not res.is_success:
            raise EidetError(res.status_code, res.text)
        return res.json()

    def _delete(self, path: str, params: dict[str, str] | None = None) -> dict[str, Any]:
        res = self._client.delete(path, params=params)
        if not res.is_success:
            raise EidetError(res.status_code, res.text)
        return res.json()
