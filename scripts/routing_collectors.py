"""Card 55 section 3.8 paging, in memory only.

Transports supply decoded report bodies or daemon JSON error envelopes.
Successful JSON and AUTONOMY_PROBE envelopes are decoded by the transport.
This is the shared page/request layer; scenario adapters remain separate work.
"""
from __future__ import annotations

import json
import re

try:
    from .routing_evidence import Missing
except ImportError:
    from routing_evidence import Missing


PLAN = "rust-route-plans"
CLIENT = "rust-route-client"


class PageRefused(Missing):
    """A responder refusal is unavailable evidence, never a plan mismatch."""

    def __init__(self, reason):
        self.reason = reason
        super().__init__(f"navigation page refused: {reason}")


def _uint(value, bits=64, positive=False):
    if type(value) is not int or not int(positive) <= value < 1 << bits:
        raise Missing("invalid navigation page number")
    return value


def _number(text, bits=32, epoch=False):
    if not re.fullmatch(r"[1-9][0-9]*" if epoch else r"[0-9]+", text):
        raise Missing("invalid navigation page number")
    return _uint(int(text), bits, positive=epoch)


def parse_page(body):
    """Parse either producer's decoded ASCII page without changing Rt/Vp bytes."""
    try:
        text = body.decode("ascii") if isinstance(body, bytes) else body
        size = len(text.encode("ascii"))
    except (UnicodeError, AttributeError) as error:
        raise Missing("non-ASCII navigation page") from error
    if text.startswith("{"):
        try:
            envelope = json.loads(text)
        except json.JSONDecodeError as error:
            raise Missing("invalid daemon error envelope") from error
        if envelope.get("ok") is not False or not isinstance(envelope.get("error"), str):
            raise Missing("invalid daemon error envelope")
        text = envelope["error"]
    refusal = re.fullmatch(r"rust-route-(?:plans|client)=(?:blocked;reason=([a-z-]+)|(missing))", text)
    if refusal:
        raise PageRefused(refusal[1] or refusal[2])
    if size > 1900:
        raise Missing("navigation page exceeds carrier")
    header, *frames = text.split("\n")
    fields = header.split(";")
    if fields[0] not in (f"{PLAN}=page", f"{CLIENT}=page"):
        raise Missing("invalid navigation page header")
    kind = fields[0].split("=")[0]
    keys = ["epoch", "corr", "actor"]
    if kind == CLIENT:
        keys += ["legs_total", "received_legs", "executed_count"]
    keys += ["cursor", "count"]
    if kind == PLAN:
        keys += ["total"]
    keys += ["next", "more"]
    if kind == CLIENT:
        keys += ["WalkPhase", "invalid_progressions", "executed_slice"]
    pairs = [field.split("=", 1) for field in fields[1:]]
    if len(pairs) != len(keys) or any(len(pair) != 2 for pair in pairs):
        raise Missing("invalid navigation page fields")
    if [pair[0] for pair in pairs] != keys:
        raise Missing("navigation page requires epoch first and unique fields")
    values = dict(pairs)
    page = {"kind": kind}
    for key in keys:
        if key not in ("WalkPhase", "executed_slice"):
            page[key] = _number(values[key], 64 if key in ("epoch", "corr", "actor") else 32,
                                epoch=key == "epoch")
    if not page["corr"] or not page["actor"]:
        raise Missing("invalid navigation page identity")
    if kind == PLAN:
        page["frames"] = frames
        total = page["total"]
        limit = 16
        if len(frames) != page["count"]:
            raise Missing("navigation page frame count")
    else:
        if frames or not re.fullmatch(r"\[(?:[0-9]+(?:,[0-9]+)*)?\]", values["executed_slice"]):
            raise Missing("invalid navigation executed slice")
        indices = values["executed_slice"][1:-1]
        page["executed_slice"] = [_number(value) for value in indices.split(",")] if indices else []
        page["WalkPhase"] = values["WalkPhase"]
        total = page["executed_count"]
        limit = 128
        if len(page["executed_slice"]) != page["count"]:
            raise Missing("navigation page executed count")
    if (page["count"] > limit or page["next"] != page["cursor"] + page["count"]
            or page["next"] > total or page["more"] != int(page["next"] < total)
            or (not page["count"] and page["more"])):
        raise Missing("invalid navigation page cursor")
    return page


def _request_epoch(cursor, epoch):
    _uint(cursor, 32)
    if epoch is None:
        if cursor:
            raise PageRefused("epoch-required")
        return ""
    return str(_uint(epoch, positive=True))


def daemon_plan_request(corr, cursor=0, maximum=16, epoch=None):
    """Build the front-door-local command; the transport adds its framing."""
    suffix = _request_epoch(cursor, epoch)
    command = f"RUST_ROUTE_PLANS {_uint(corr, positive=True)} {cursor} {_uint(maximum, 32)}"
    return command + (f" {suffix}" if suffix else "")


def client_page_request(bridge_corr, kind, corr, actor, cursor=0, maximum=16, epoch=None):
    """Build the exact v11 command for either client page kind."""
    if kind not in (PLAN, CLIENT) or not re.fullmatch(r"[0-9a-fA-F]{32}", bridge_corr):
        raise Missing("invalid navigation page request")
    suffix = _request_epoch(cursor, epoch)
    command = (f"SACS_RECORDER|{bridge_corr}|{kind}|{_uint(corr, positive=True)}|"
               f"{_uint(actor)}|{cursor}|{_uint(maximum, 32)}")
    return command + (f"|{suffix}" if suffix else "")


def collect_pages(request_page, kind, corr, actor=0, *, daemon_epoch=None):
    """Call request_page(cursor, epoch) and return one complete in-memory receipt.

    An epoch refusal or a changed page epoch discards the partial receipt and
    restarts once at cursor zero. Continued churn raises Missing (BLOCKED).
    daemon_epoch is the last known daemon observation; only older evidence blocks.
    Callers must compare the returned epoch across daemon/client receipts too.
    """
    if kind not in (PLAN, CLIENT):
        raise Missing("invalid navigation page kind")
    _uint(corr, positive=True)
    _uint(actor)
    if daemon_epoch is not None:
        _uint(daemon_epoch, positive=True)
    payload = "frames" if kind == PLAN else "executed_slice"
    for attempt in range(2):
        cursor, epoch, receipt = 0, None, None
        try:
            while True:
                page = parse_page(request_page(cursor, epoch))
                if (page["kind"] != kind or page["corr"] != corr
                        or (actor and page["actor"] != actor) or page["cursor"] != cursor):
                    raise Missing("foreign navigation page")
                if epoch is not None and page["epoch"] != epoch:
                    raise PageRefused("epoch-mismatch")
                if daemon_epoch is not None and page["epoch"] < daemon_epoch:
                    raise Missing("navigation evidence older than daemon epoch")
                if receipt is None:
                    epoch = page["epoch"]
                    receipt = dict(page)
                    receipt[payload] = []
                else:
                    stable = ("actor", "total") if kind == PLAN else (
                        "actor", "legs_total", "received_legs", "executed_count",
                        "WalkPhase", "invalid_progressions")
                    if any(page[key] != receipt[key] for key in stable):
                        raise Missing("navigation receipt changed during paging")
                receipt[payload].extend(page[payload])
                cursor = page["next"]
                if not page["more"]:
                    receipt.update(cursor=0, count=cursor, next=cursor, more=0)
                    return receipt
        except PageRefused as error:
            if attempt or error.reason not in ("epoch-required", "epoch-mismatch"):
                raise
