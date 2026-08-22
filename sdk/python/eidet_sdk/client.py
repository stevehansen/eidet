"""Eidet Python SDK — REST API client for Eidet memory service."""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Any

import httpx


class MemoryType(str, Enum):
    OBSERVATION = "observation"
    INSIGHT = "insight"
    PROCEDURE = "procedure"
    HEURISTIC = "heuristic"


class Valence(str, Enum):
    NEUTRAL = "neutral"
    AFFIRMING = "affirming"
    REFUTING = "refuting"
    CAUTIONARY = "cautionary"


class FunctionalStage(str, Enum):
    """Functional subtask a memory applies to; a memory with no stage matches every stage."""

    ANALYZE = "analyze"
    LOCATE = "locate"
    EDIT = "edit"
    TEST = "test"
    DEBUG = "debug"
    DEPLOY = "deploy"


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
    negative: bool = False
    valence: Valence | str | None = None
    stage: FunctionalStage | str | None = None


class EidetError(Exception):
    def __init__(self, status: int, body: str) -> None:
        self.status = status
        self.body = body
        super().__init__(f"Eidet API error {status}: {body}")


_MAINTENANCE_POLL_SECONDS = 5.0
_MAINTENANCE_POST_TIMEOUT = 60.0


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
        negative: bool = False,
        valence: Valence | str | None = None,
        stage: FunctionalStage | str | None = None,
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
        if negative:
            body["negative"] = True
        if valence:
            body["valence"] = str(valence.value) if isinstance(valence, Valence) else valence
        if stage:
            body["stage"] = str(stage.value) if isinstance(stage, FunctionalStage) else stage
        return self._post("/api/eidet", body)

    def recall(
        self,
        repo: str,
        query: str,
        *,
        limit: int = 10,
        type: MemoryType | str | None = None,
        tags: list[str] | None = None,
        valence: Valence | str | None = None,
        stage: FunctionalStage | str | None = None,
        cross_repo: bool = False,
    ) -> list[dict[str, Any]]:
        params: dict[str, str] = {"repo": repo, "q": query, "limit": str(limit)}
        if type:
            params["type"] = str(type.value) if isinstance(type, MemoryType) else type
        if tags:
            params["tags"] = ",".join(tags)
        if valence:
            params["valence"] = str(valence.value) if isinstance(valence, Valence) else valence
        if stage:
            params["stage"] = str(stage.value) if isinstance(stage, FunctionalStage) else stage
        if cross_repo:
            params["cross_repo"] = "true"
        data = self._get("/api/eidet/search", params=params)
        return data["results"]

    def context(self, repo: str) -> str:
        data = self._get("/api/eidet/context", params={"repo": repo})
        return data["context"]

    def get_memory(self, memory_id: str) -> dict[str, Any]:
        return self._get(f"/api/eidet/{memory_id}")

    def update(
        self,
        memory_id: str,
        *,
        content: str | None = None,
        tags: list[str] | None = None,
        importance: float | None = None,
        confidence: float | None = None,
        type: MemoryType | str | None = None,
        stage: FunctionalStage | str | None = None,
        one_liner: str | None = None,
        summary: str | None = None,
        foresight_hint: str | None = None,
        expected_content_sha256: str | None = None,
    ) -> dict[str, Any]:
        """Update a memory. Content changes create a versioned supersession; a stale
        expected_content_sha256 precondition raises EidetError with status 409."""
        body: dict[str, Any] = {}
        if content is not None:
            body["content"] = content
        if tags is not None:
            body["tags"] = tags
        if importance is not None:
            body["importance"] = importance
        if confidence is not None:
            body["confidence"] = confidence
        if type is not None:
            body["type"] = str(type.value) if isinstance(type, MemoryType) else type
        if stage is not None:
            body["stage"] = str(stage.value) if isinstance(stage, FunctionalStage) else stage
        if one_liner is not None:
            body["oneLiner"] = one_liner
        if summary is not None:
            body["summary"] = summary
        if foresight_hint is not None:
            body["foresightHint"] = foresight_hint
        if expected_content_sha256 is not None:
            body["expectedContentSha256"] = expected_content_sha256
        return self._put(f"/api/eidet/{memory_id}", body)

    def redact(self, memory_id: str, reason: str) -> bool:
        """Scrub a memory's content to a tombstone (audit node preserved)."""
        data = self._post(f"/api/eidet/{memory_id}/redact", {"reason": reason})
        return data.get("redacted", False)

    def forget(self, memory_id: str, reason: str | None = None) -> bool:
        params = {"reason": reason} if reason else {}
        data = self._delete(f"/api/eidet/{memory_id}", params=params)
        return data.get("forgotten", False)

    def feedback(self, memory_id: str, was_used: bool, reason: str | None = None) -> bool:
        body: dict[str, Any] = {"memoryId": memory_id, "wasUsed": was_used}
        if reason:
            body["reason"] = reason
        data = self._post("/api/eidet/feedback", body)
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

    def intake_git(
        self,
        repo: str,
        *,
        since: str | None = None,
        max_commits: int | None = None,
        all_commits: bool = False,
        dry_run: bool = False,
    ) -> dict[str, Any]:
        url = f"/api/eidet/intake/git?repo={repo}"
        if since:
            url += f"&since={since}"
        if max_commits is not None:
            url += f"&max_commits={max_commits}"
        if all_commits:
            url += "&all_commits=true"
        if dry_run:
            url += "&dry_run=true"
        return self._post(url)

    def intake_claude_memory(self, repo: str, *, dry_run: bool = False) -> dict[str, Any]:
        """Import Claude Code's native per-project memory (MEMORY.md) as seed memories."""
        url = f"/api/eidet/intake/claude-memory?repo={repo}"
        if dry_run:
            url += "&dry_run=true"
        return self._post(url)

    def consolidate(self, repo: str) -> dict[str, Any]:
        return self._post(f"/api/eidet/consolidate?repo={repo}")

    def maintenance(self, repo: str) -> dict[str, Any]:
        """Run the maintenance pipeline.

        A pass that outlives the service's grace window is handed back as a run id to poll; this
        follows it to the end, so the return value is always the finished report — a slow repo takes
        longer, it does not fail. The POST gets its own timeout because the client-wide one is the
        same length as the grace window.
        """
        res = self._client.post(f"/api/maintenance?repo={repo}", timeout=_MAINTENANCE_POST_TIMEOUT)
        if not res.is_success:
            raise EidetError(res.status_code, res.text)

        body = res.json()
        if res.status_code != 202:
            return body

        while True:
            time.sleep(_MAINTENANCE_POLL_SECONDS)
            run = self._get(body["poll"])
            if run["status"] == "running":
                continue
            if run["status"] == "failed":
                raise EidetError(500, run.get("error") or "maintenance failed")
            return run.get("report") or {}

    def export_markdown(self, repo: str, format: str | None = None) -> str:
        """Render memories as markdown; format="agents" renders the AGENTS.md interop shape."""
        params = {"repo": repo}
        if format:
            params["format"] = format
        res = self._client.get("/api/eidet/export", params=params)
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

    def is_available(self) -> bool:
        try:
            self.health()
            return True
        except Exception:
            return False

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

    def _put(self, path: str, body: dict[str, Any]) -> dict[str, Any]:
        res = self._client.put(path, json=body)
        if not res.is_success:
            raise EidetError(res.status_code, res.text)
        return res.json()

    def _delete(self, path: str, params: dict[str, str] | None = None) -> dict[str, Any]:
        res = self._client.delete(path, params=params)
        if not res.is_success:
            raise EidetError(res.status_code, res.text)
        return res.json()
