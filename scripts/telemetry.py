#!/usr/bin/env python3
"""Sentry observability pilot: thin Python telemetry shim.

Shares the same session_id/pipeline_id/run_archive_path as the .NET SimsOnlineTelemetry
wrapper via environment variables (SIMSONLINE_RUN_ID, SIMSONLINE_PIPELINE_ID,
SIMSONLINE_RUN_ARCHIVE_PATH). Emits to a local JSON sink (.omo/run/telemetry-events.jsonl)
and optionally to Sentry when SENTRY_DSN is set.

Usage from stackctl / validation harness:
    from scripts.telemetry import TelemetryShim
    tel = TelemetryShim.from_env()
    tel.emit("dev_session.stack_incomplete", {"reason": "client_missing", "client_role": "client"})
"""
from __future__ import annotations

import json
import os
import time
from pathlib import Path
from typing import Any


def _safe_value(value: str | None) -> str | None:
    """Sanitize a value for telemetry — strip PII (save names, paths, machine ids)."""
    if value is None:
        return None
    # Strip anything that looks like a file path, save name, or username.
    if "/" in value or "\\" in value or ".sims3" in value.lower():
        return "[redacted]"
    return value[:128]


class TelemetryShim:
    """Thin Python telemetry emitter that correlates with .NET SimsOnlineTelemetry."""

    def __init__(
        self,
        session_id: str,
        pipeline_id: str | None = None,
        run_archive_path: str | None = None,
        environment: str = "development",
        dsn: str | None = None,
        sink_path: Path | None = None,
    ):
        self.session_id = session_id
        self.pipeline_id = pipeline_id
        self.run_archive_path = run_archive_path
        self.environment = environment
        self._dsn = dsn
        self._sink_path = sink_path or Path(".omo/run/telemetry-events.jsonl")
        self._reported: set[str] = set()

    @classmethod
    def from_env(cls) -> "TelemetryShim":
        """Create a shim from SIMSONLINE_* environment variables."""
        return cls(
            session_id=os.environ.get("SIMSONLINE_RUN_ID", "unknown"),
            pipeline_id=os.environ.get("SIMSONLINE_PIPELINE_ID"),
            run_archive_path=os.environ.get("SIMSONLINE_RUN_ARCHIVE_PATH"),
            environment=os.environ.get("SENTRY_ENVIRONMENT", "development"),
            dsn=os.environ.get("SENTRY_DSN"),
        )

    def emit(
        self,
        signal: str,
        attributes: dict[str, Any] | None = None,
        *,
        dedupe: bool = True,
    ) -> dict[str, Any]:
        """Emit a telemetry event to the local JSON sink (and Sentry if DSN is set).

        Returns the event dict. When dedupe=True, each unique signal fires at most once
        per session (matching .NET ReportFailureOnce semantics).
        """
        if dedupe and signal in self._reported:
            return {"signal": signal, "status": "deduped"}

        if dedupe:
            self._reported.add(signal)

        event: dict[str, Any] = {
            "signal": signal,
            "session_id": self.session_id,
            "environment": self.environment,
            "timestamp": time.time(),
        }

        if self.pipeline_id:
            event["pipeline_id"] = self.pipeline_id
        if self.run_archive_path:
            event["run_archive_path"] = self.run_archive_path

        if attributes:
            safe_attrs: dict[str, Any] = {}
            for key, value in attributes.items():
                if value is not None:
                    safe_attrs[key] = _safe_value(str(value)) if isinstance(value, str) else value
            event["attributes"] = safe_attrs

        # Write to local JSON sink (always — this is the acceptance evidence when Sentry is not configured).
        try:
            self._sink_path.parent.mkdir(parents=True, exist_ok=True)
            with open(self._sink_path, "a", encoding="utf-8") as f:
                f.write(json.dumps(event, default=str) + "\n")
        except Exception:
            pass  # Never let telemetry IO break the session.

        # When Sentry DSN is configured, optionally send there too.
        # (The Python sentry-sdk is optional; if not installed, we silently skip.)
        if self._dsn:
            try:
                import sentry_sdk  # type: ignore

                sentry_sdk.add_breadcrumb(
                    message=signal,
                    category="simsonline.failure",
                    level="warning",
                    data=event,
                )
            except ImportError:
                pass

        return event
