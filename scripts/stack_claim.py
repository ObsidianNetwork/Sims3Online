"""Who is using the live dev stack: one claim line shared by every session on this PC (warn-only).

Several sessions can work at once (a decompile job, a test series, a debugger on one game), but there is one pair of
games. A session that boots, restarts, drives or attaches to the games first takes the claim; the others read it and
wait. The claim is the file .omo/run/stack-claim.txt in the main checkout (every worktree of the repo reads the same
one), one line `<session> <ISO time> <purpose>` (the convention the handoffs already use, so a hand-written line counts
too). Nothing is locked: quick_loop's boots and
scenarios.py runs print a warning when another session holds it, and a claim older than STALE_HOURS is reported as
stale (its session probably ended without releasing it).

    python scripts/stack_claim.py                                   # who holds it, or free
    python scripts/stack_claim.py take breakpoint on 004fa800 in the server game   # in a Claude session: its own id
    python scripts/stack_claim.py release
    python scripts/stack_claim.py take f7f5a276 seated socials fork change   # a shell outside a session: the session first
    SIMSONLINE_SESSION=f7f5a276 python scripts/stack_claim.py take seated socials   # for another session

A tool knows its own session (own_session): SIMSONLINE_SESSION, else the first 8 characters of CLAUDE_CODE_SESSION_ID,
which every command a Claude Code session runs inherits (the claim ids of 2026-10-21, f7f5a276, 19c050d2, caa546bd,
are that prefix). So a session's own tools and the subprocesses they start know its claim as its own: on 2026-10-21 a
rejoin subprocess under Session E's own claim warned "claimed by 19c050d2 ... shares their games" until E set
SIMSONLINE_SESSION by hand. An OmO session is the last 8 hex digits of PI_SESSION_ID (OMO_SIMSONLINE_SESSION in a
lane, whose host drops SIMSONLINE_* names); with both PI_SESSION_ID and CLAUDE_CODE_SESSION_ID set and no explicit
session, take/queue/wait/release refuse (exit 2) rather than guess. Neither set (a shell outside a session): any claim
is someone else's. `stackctl status`
leaves the bridge alone (listeners only) while another session holds it, and it and the session brief name the holder
(holder(): a quiet window as one, and the line behind the claim) and this session's id; the holder's own brief shows
who waits behind it (waiting()). The SessionEnd hook (scripts/hooks/session_end.py) takes an ending session out of
the line and, when it still holds the claim, adds a second line `ended <ISO time> <reason>` (mark_ended) that
holder(), status and the quiet-window refusal repeat, so the others know a `release --force` is safe once its games
are idle; the claim itself stays (a series may still be running, and a resumed session keeps its id).

With the session known, every word of take is the purpose (a first word equal to the session is dropped; a first word
that is another session's 8-character id is refused: claim for another session with SIMSONLINE_SESSION=<it>), and
release needs no session. Without it, take's first word is the session, one word; the rest is the purpose. A first
word with spaces is refused: it is a purpose given without its session (2026-10-21: `take "crumple fix: ..."` claimed
the stack for a session named "crumple fix: ..."). A claim line written by hand with a session of several words still
reads (parse), and release takes such a session quoted.

The line and the quiet window replace brokering the stack and build slots by message through Main (tooling review
2026-10-21, #6: 56 of Main's 231 incoming messages were about slots):

    python scripts/stack_claim.py queue <purpose>        # wait in line (stack-queue.txt beside the claim, first come first)
    python scripts/stack_claim.py wait [--timeout S]     # block until it is free and this session is first, then take it
    python scripts/stack_claim.py take --quiet <purpose> # a quiet window: other sessions' builds refuse to run
    python scripts/stack_claim.py check-quiet            # exit 3 with the reason during another session's quiet window

take refuses while another session is ahead in line (--force goes first anyway); release prints who is next, so the
releaser can tell them, and their `wait` takes it on its own (with no --timeout it waits as long as it takes: run it
in the background). A first in line that does not take the free stack within TURN_MINUTES (10) of its turn is dropped
from the line by the next take, queue, release, status or wait, and the drop is recorded (stack-dropped.txt beside the
claim) with the session and the times, which status and the session brief show for STALE_HOURS (2026-10-21: Q lost
30 minutes behind a waiter whose session had gone). A quiet window is a claim whose purpose starts with
[quiet] (so the session brief and stackctl status show it): check_mod_runtime.py, make-mod.ps1, dev.py's .NET tests and
bridgectl's probe compiles refuse during another session's (an arms series timing the games, whose results a
compile's CPU load skews), unless given --force (make-mod: -Force) or SIMSONLINE_QUIET_OK=1, and the git_guard hook
refuses a hand-typed dotnet build/test/run, csc or msbuild (SIMSONLINE_QUIET_OK=1 <command> overrides). Nothing is locked: two
sessions writing the line at the same moment can lose one entry, and status shows each entry's age.
Tests: python -m unittest scripts.stack_claim_test
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timedelta
from pathlib import Path

def main_checkout() -> Path:
    """The repo's main checkout (the parent of git's common directory), the same from every worktree; this checkout
    when git cannot say."""
    here = Path(__file__).resolve().parent.parent
    try:
        common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=here,
                                capture_output=True, text=True, timeout=10).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return here
    return Path(common).parent if common else here


CLAIM = main_checkout() / ".omo" / "run" / "stack-claim.txt"
STALE_HOURS = 4
DOWN_REFUSED = "stackctl down refused"  # the first words of down_refusal()


ISO_TIME = re.compile(r"\d{4}-\d{2}-\d{2}[T ]?\S*$")


def parse(line: str) -> tuple[str, datetime, str] | None:
    """(session, when, purpose) from a claim line. The session is every word before the first ISO time, so a session
    named with spaces ("Session C: ") needs no quotes; quotes around it are dropped."""
    words = line.strip().split(" ")
    for i, word in enumerate(words[1:], 1):
        if not ISO_TIME.match(word):
            continue
        try:
            when = datetime.fromisoformat(word)
        except ValueError:
            continue
        if when.tzinfo is not None:  # a hand-written line with an offset: compare in local time
            when = when.astimezone().replace(tzinfo=None)
        return " ".join(words[:i]).strip('"'), when, " ".join(words[i + 1:]).strip()
    return None


def format_line(session: str, when: datetime, purpose: str) -> str:
    return f"{session} {when.isoformat(timespec='seconds')} {purpose}".rstrip()


def read(path: Path = CLAIM) -> tuple[str, datetime, str] | None:
    try:
        return parse(path.read_text(encoding="utf-8").splitlines()[0])
    except (OSError, IndexError):
        return None


def describe(claim: tuple[str, datetime, str], now: datetime) -> str:
    session, when, purpose = claim
    hours = (now - when).total_seconds() / 3600
    stale = f" (stale: over {STALE_HOURS} h old, its session may have ended)" if now - when > timedelta(hours=STALE_HOURS) else ""
    return f"the live stack is claimed by {session} since {when:%H:%M} ({hours:.1f} h): {purpose or 'no purpose given'}{stale}"


def warning(session: str | None = None, path: Path = CLAIM, now: datetime | None = None) -> str | None:
    """A line to print when someone other than session holds the claim, else None. Never raises. A file that is not a
    claim line still warns (someone wrote it by hand), quoting it."""
    try:
        first = path.read_text(encoding="utf-8").strip().splitlines()[0]
    except (OSError, IndexError):
        return None
    claim = parse(first)
    if claim is None:
        return f"WARNING: the live stack claim file holds {first[:120]!r}, not `<session> <ISO time> <purpose>`: someone may be using the games (scripts/stack_claim.py)"
    if session and claim[0] == session:
        return None
    return "WARNING: " + describe(claim, now or datetime.now()) + "; this shares their games (scripts/stack_claim.py)"


def holder(session: str | None = None, path: Path | None = None, now: datetime | None = None) -> str | None:
    """Who holds the claim when it is not session, as "D since 21:30 (gc gate series)" (a stale claim says so), else
    None: stackctl status skips its bridge probes for them and the session brief names them. A quiet window is named as
    one, an ended session (the SessionEnd hook's mark) says so, and the line behind the claim follows ("; 2 in line: E
    (since 21:40), ..."), read beside path. Never raises. A file that is not a claim line counts as held (someone wrote
    it by hand), quoted. The path is read at call time (CLAIM)."""
    claim_path = CLAIM if path is None else path
    try:
        first = claim_path.read_text(encoding="utf-8").strip().splitlines()[0]
    except (OSError, IndexError):
        return None
    claim = parse(first)
    if claim is None:
        return f"someone (the claim file holds {first[:60]!r})"
    who, when, purpose = claim
    if session and who == session:
        return None
    stale = (now or datetime.now()) - when > timedelta(hours=STALE_HOURS)
    quiet = is_quiet(purpose)
    shown = purpose[len(QUIET):].strip() if quiet else purpose
    return (f"{who} since {when:%H:%M}" + (f" ({shown[:60]})" if shown else "")
            + (", a quiet window: no builds, csc or probe compiles from other sessions" if quiet else "")
            + (f", stale (over {STALE_HOURS} h old)" if stale else "") + ended_note(claim_path)
            + in_line(session, claim_path))


def ended(path: Path | None = None) -> tuple[datetime, str] | None:
    """When the claim's session ended and why (the SessionEnd hook's second line `ended <ISO time> <reason>`), or None."""
    try:
        lines = (CLAIM if path is None else path).read_text(encoding="utf-8").splitlines()
    except OSError:
        return None
    mark = next((entry for entry in map(parse, lines[1:]) if entry and entry[0] == ENDED), None)
    return (mark[1], mark[2]) if mark else None


def ended_note(path: Path | None = None) -> str:
    mark = ended(path)
    return (f", its session ended at {mark[0]:%H:%M}" + (f" ({mark[1]})" if mark[1] else "")
            + ": `stack_claim.py release --force` once its games are idle") if mark else ""


def mark_ended(session: str, reason: str = "", now: datetime | None = None, path: Path | None = None) -> bool:
    """For the SessionEnd hook: when session holds the claim, keep it (the games may be mid-series, and a resumed
    session keeps its id) but add the `ended` line, so holder(), status and quiet_refusal tell the others. True when
    marked. A take rewrites the file, so the mark goes with the next claim."""
    claim_path = CLAIM if path is None else path
    claim = current(claim_path)
    if claim is None or claim[0] != session:
        return False
    claim_path.write_text(format_line(*claim) + "\n" + format_line(ENDED, now or datetime.now(), reason) + "\n",
                          encoding="utf-8")
    return True


def in_line(session: str | None = None, path: Path | None = None) -> str:
    """"; 2 in line: E (since 21:40), F (this session, since 21:45)" for the queue beside the claim path, else ""."""
    queue = read_queue(queue_path(path))
    if not queue:
        return ""
    shown = [f"{who} ({'this session, ' if who == session else ''}since {when:%H:%M})" for who, when, _ in queue[:3]]
    return f"; {len(queue)} in line: " + ", ".join(shown) + (", ..." if len(queue) > 3 else "")


def waiting(session: str | None, path: Path | None = None) -> str | None:
    """For the holder itself (holder() is None for it): "2 in line behind it: ..." when session holds the claim and
    others wait, else None."""
    claim = current(path)
    if not session or claim is None or claim[0] != session:
        return None
    line = in_line(session, path)
    return line[2:].replace(" in line:", " in line behind it:", 1) if line else None


CLAIM_ID = re.compile(r"[0-9a-f]{8}")  # a Claude session's claim id: the first 8 characters of its session id


def down_refusal(held: str) -> str:
    """stackctl down's refusal while another session (held: holder()'s words) has the claim; quick_loop and saves.py
    pass it on as it is. Main, 2026-10-22: booting or ending games without the claim is the bug, not the orphaning a
    down from the wrong checkout used to leave. A stale holder is the main session's to clear (release --force)."""
    return (f"{DOWN_REFUSED}: the live stack is claimed by {held}; no process touched. Take the claim first "
            "(python scripts/stack_claim.py take <purpose>, or queue <purpose>), or pass --ignore-claim")


def own_session(environ=None) -> str | None:
    """This session's claim id: OMO_SIMSONLINE_SESSION or SIMSONLINE_SESSION (an OmO lane host keeps only OMO_* names),
    else the last 8 hex digits of OmO's PI_SESSION_ID, else the first 8 characters of CLAUDE_CODE_SESSION_ID or
    ANTIGRAVITY_CONVERSATION_ID (set in every command an agent session runs, and inherited by the subprocesses its tools
    start), else None. Both PI_SESSION_ID and CLAUDE_CODE_SESSION_ID set (a Claude session started from an OmO shell
    inherits the OmO id, or the reverse) is ambiguous: None, and session_conflict() says why."""
    env = os.environ if environ is None else environ
    explicit = env.get("OMO_SIMSONLINE_SESSION") or env.get("SIMSONLINE_SESSION")
    if explicit:
        return explicit
    if session_conflict(env):
        return None
    return (
        omo_session_id(env.get("PI_SESSION_ID") or "")
        or (env.get("CLAUDE_CODE_SESSION_ID") or "")[:8]
        or (env.get("ANTIGRAVITY_CONVERSATION_ID") or "")[:8]
        or None
    )


def omo_session_id(pi_session_id: str) -> str:
    """An OmO session's claim id: the last 8 hex digits of PI_SESSION_ID (its durableSessionId; OmO ids share a time
    prefix, so the first 8 would collide between sessions started close together)."""
    digits = re.sub(r"[^0-9a-fA-F]", "", pi_session_id).lower()
    return digits[-8:] if len(digits) >= 8 else ""


def session_conflict(environ=None) -> str | None:
    """Why this process's session is ambiguous (both an OmO and a Claude session id, no explicit session), or None."""
    env = os.environ if environ is None else environ
    if env.get("OMO_SIMSONLINE_SESSION") or env.get("SIMSONLINE_SESSION"):
        return None
    if env.get("PI_SESSION_ID") and env.get("CLAUDE_CODE_SESSION_ID"):
        return (f"both PI_SESSION_ID (OmO {omo_session_id(env['PI_SESSION_ID']) or '?'}) and CLAUDE_CODE_SESSION_ID "
                f"(Claude {env['CLAUDE_CODE_SESSION_ID'][:8]}) are set: one was inherited from the shell that started "
                f"this session. Set SIMSONLINE_SESSION (OMO_SIMSONLINE_SESSION in an OmO lane) to say which")
    return None


def take_words(words: list[str], env_session: str | None) -> tuple[str, str] | str:
    """take's words as (session, purpose), or why they are refused. With env_session (own_session) every word is the
    purpose, less a first word equal to the session; a first word that is another session's claim id is refused (a
    handoff's example id copied, or a claim meant for another session). Without it the first word is the session and
    must be one word with no ISO time in it: a first word with spaces is a purpose given without its session."""
    if env_session:
        if words and CLAIM_ID.fullmatch(words[0]) and words[0] != env_session:
            return (f"take: {words[0]} is another session's id and this session is {env_session} (SIMSONLINE_SESSION, "
                    f"else CLAUDE_CODE_SESSION_ID / ANTIGRAVITY_CONVERSATION_ID): take <purpose>; to claim for that session, set "
                    f"SIMSONLINE_SESSION={words[0]}")
        return env_session, " ".join(words[1:] if words and words[0] == env_session else words)
    if not words or not words[0].strip():
        return "take: give the session first: take <session> <purpose> (or set SIMSONLINE_SESSION)"
    session = words[0]
    if len(session.split()) > 1 or ISO_TIME.match(session):
        return (f"take: the session is one word (e.g. the first 8 characters of this Claude session's id); {session!r} "
                f"looks like a purpose: take <session> {session!r}, or set SIMSONLINE_SESSION")
    return session, " ".join(words[1:])


QUIET = "[quiet]"  # a claim whose purpose starts with this is a quiet window (quiet_refusal)
QUIET_OK = "SIMSONLINE_QUIET_OK"  # =1 lets a build run in another session's quiet window
QUEUE_NAME = "stack-queue.txt"
ENDED = "ended"  # the claim file's second line after the holder's session ended (mark_ended)
WAIT_EVERY = 15.0
TURN_MINUTES = 10  # a first in line that does not take the free stack within this is dropped (advance)
TURN_NAME = "stack-turn.txt"
DROPPED_NAME = "stack-dropped.txt"
DROPPED_KEEP = 20


def current(path: Path | None = None) -> tuple[str, datetime, str] | None:
    """The claim now; the path is read at call time (read()'s default is bound when the module loads)."""
    try:
        return parse((CLAIM if path is None else path).read_text(encoding="utf-8").splitlines()[0])
    except (OSError, IndexError):
        return None


def queue_path(claim: Path | None = None) -> Path:
    """The line of sessions waiting for the stack, beside the claim: one claim-format line each, first come first."""
    return (CLAIM if claim is None else claim).with_name(QUEUE_NAME)


def read_queue(path: Path | None = None) -> list[tuple[str, datetime, str]]:
    try:
        lines = (path or queue_path()).read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    return [entry for entry in map(parse, lines) if entry]


def write_queue(entries: list[tuple[str, datetime, str]], path: Path | None = None) -> None:
    path = path or queue_path()
    if not entries:
        path.unlink(missing_ok=True)
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(format_line(*entry) + "\n" for entry in entries), encoding="utf-8")


def ahead(session: str, queue: list[tuple[str, datetime, str]]) -> list[tuple[str, datetime, str]]:
    """The entries before session's own (all of them when it is not in line)."""
    names = [entry[0] for entry in queue]
    return queue[:names.index(session)] if session in names else list(queue)


def entry_text(entry: tuple[str, datetime, str], now: datetime) -> str:
    session, when, purpose = entry
    return f"{session} (in line since {when:%H:%M}, {(now - when).total_seconds() / 3600:.1f} h: {purpose or 'no purpose given'})"


def is_quiet(purpose: str) -> bool:
    return purpose.startswith(QUIET)


def quiet_purpose(purpose: str) -> str:
    return purpose if is_quiet(purpose) else f"{QUIET} {purpose}".rstrip()


def quiet_refusal(session: str | None = None, path: Path | None = None, now: datetime | None = None,
                  environ=None) -> str | None:
    """Why a build (csc, dotnet build or test, make-mod, a probe compile) should not run now: another session holds the
    stack in a quiet window (take --quiet). None when it may: no claim, an ordinary claim, this session's own (session,
    default own_session), or SIMSONLINE_QUIET_OK=1. Never raises."""
    env = os.environ if environ is None else environ
    if env.get(QUIET_OK) == "1":
        return None
    claim = current(path)
    if claim is None or not is_quiet(claim[2]):
        return None
    mine = own_session(env) if session is None else session
    if mine and claim[0] == mine:
        return None
    return ("REFUSED: " + describe(claim, now or datetime.now()) + ended_note(path) + "; it is a quiet window: no "
            "builds, csc or probe "
            "compiles from other sessions until it ends. Wait in line: scripts/stack_claim.py queue <purpose>, then "
            f"stack_claim.py wait; to build anyway: --force (make-mod.ps1 -Force) or {QUIET_OK}=1")


def refuse_build(force: bool = False) -> bool:
    """For a build tool: print why it should not run and return True during another session's quiet window."""
    refusal = None if force else quiet_refusal()
    if refusal:
        print(refusal, flush=True)
    return bool(refusal)


def claim_for(session: str, purpose: str, now: datetime) -> None:
    """Write the claim and take session out of the line."""
    CLAIM.parent.mkdir(parents=True, exist_ok=True)
    CLAIM.write_text(format_line(session, now, purpose) + "\n", encoding="utf-8")
    turn_path().unlink(missing_ok=True)
    queue = read_queue()
    if any(entry[0] == session for entry in queue):
        write_queue([entry for entry in queue if entry[0] != session])


def turn_path(claim: Path | None = None) -> Path:
    """`<session> <ISO time>`: who is first in line with the stack free, and since when (advance)."""
    return (CLAIM if claim is None else claim).with_name(TURN_NAME)


def dropped_path(claim: Path | None = None) -> Path:
    """The last DROPPED_KEEP entries advance dropped, a claim-format line each (the purpose says whose turn it was)."""
    return (CLAIM if claim is None else claim).with_name(DROPPED_NAME)


def stamp_turn(session: str, now: datetime, claim: Path | None = None) -> None:
    path = turn_path(claim)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(format_line(session, now, "") + "\n", encoding="utf-8")


def advance(now: datetime | None = None, claim: Path | None = None) -> list[str]:
    """Keep the line moving (2026-10-21: Q lost 30 minutes behind a waiter whose session had gone): while the stack is
    free, the first in line has TURN_MINUTES from when its turn came (the release, or when it queued onto a free stack,
    stamped in turn_path) to take it; after that its entry is dropped, recorded in dropped_path, and the next one's turn
    starts. A live `wait` takes the stack within WAIT_EVERY of its turn, so only a waiter that is not waiting is
    dropped. Every take, queue, release, status and wait calls this. Returns a line per drop, naming the session and
    the times."""
    now = now or datetime.now()
    notes = []
    while True:
        queue = read_queue(queue_path(claim))
        if current(claim) is not None or not queue:
            turn_path(claim).unlink(missing_ok=True)
            return notes
        first = queue[0]
        try:
            stamp = parse(turn_path(claim).read_text(encoding="utf-8").splitlines()[0])
        except (OSError, IndexError):
            stamp = None
        if stamp is None or stamp[0] != first[0]:
            stamp_turn(first[0], now, claim)
            return notes
        if now - stamp[1] < timedelta(minutes=TURN_MINUTES):
            return notes
        write_queue(queue[1:], queue_path(claim))
        record = format_line(first[0], now, f"turn since {stamp[1]:%H:%M}" + (f": {first[2]}" if first[2] else ""))
        try:
            kept = dropped_path(claim).read_text(encoding="utf-8").splitlines()[-(DROPPED_KEEP - 1):]
        except OSError:
            kept = []
        dropped_path(claim).write_text("".join(f"{line}\n" for line in kept + [record]), encoding="utf-8")
        notes.append(drop_text((first[0], now, record.split(" ", 2)[2])))
        if len(queue) > 1:
            stamp_turn(queue[1][0], now, claim)


def drop_text(entry: tuple[str, datetime, str]) -> str:
    session, when, purpose = entry
    since = (re.match(r"turn since (\d\d:\d\d)", purpose) or [None, "?"])[1]
    return (f"{session} was dropped from the stack's line at {when:%H:%M}: its turn came at {since} and it did not "
            f"take the stack within {TURN_MINUTES} min (queue again to get back in line)")


def recent_drops(now: datetime | None = None, claim: Path | None = None, hours: float = STALE_HOURS) -> list[str]:
    """drop_text for each drop in the last hours (the session brief and status show them)."""
    try:
        lines = dropped_path(claim).read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    now = now or datetime.now()
    return [drop_text(entry) for entry in map(parse, lines)
            if entry and now - entry[1] <= timedelta(hours=hours)]


def wait_turn(session: str, timeout: float | None = None, every: float = WAIT_EVERY, sleep=time.sleep,
              clock=time.time, today=datetime.now) -> int:
    """Block until the stack is free and session is first in line, then take it with its queued purpose (exit 0).
    With a timeout, 3 when it passes (wait again); without one it waits as long as it takes (run it in the background:
    a Claude Code foreground command stops at 10 minutes). 2 when session is not in line."""
    deadline = None if timeout is None else clock() + timeout
    while True:
        now = today()
        for note in advance(now):
            print(note, flush=True)
        queue = read_queue()
        mine = next((entry for entry in queue if entry[0] == session), None)
        claim = current()
        if mine is None:
            if claim and claim[0] == session:
                print(f"{session} holds the stack already")
                return 0
            mine_dropped = [text for text in recent_drops(now) if text.startswith(f"{session} ")]
            print(f"wait: {session} is not in line (stack_claim.py queue <purpose> first)"
                  + (f"; {mine_dropped[-1]}" if mine_dropped else ""))
            return 2
        before = ahead(session, queue)
        if claim is None and not before:
            claim_for(session, mine[2], now)
            print(f"your turn: claimed for {session}" + (f": {mine[2]}" if mine[2] else ""), flush=True)
            return 0
        if deadline is not None and clock() >= deadline:
            held = describe(claim, now) if claim else "the stack is free"
            print(f"{held}; {session} is {len(before) + 1} in line; still waiting after {timeout:.0f} s (wait again)",
                  flush=True)
            return 3
        sleep(every)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = p.add_subparsers(dest="cmd")
    t = sub.add_parser("take", help="claim the stack for a session: take <session> <purpose...> (SIMSONLINE_SESSION: "
                                    "take <purpose...>)")
    t.add_argument("words", nargs="*", metavar="session purpose")
    t.add_argument("--force", action="store_true", help="take it over from another session, or ahead of the line")
    t.add_argument("--quiet", action="store_true", help="a quiet window: other sessions' builds refuse to run")
    r = sub.add_parser("release", help="drop a session's claim")
    r.add_argument("session", nargs="?", help="default SIMSONLINE_SESSION")
    r.add_argument("--force", action="store_true", help="drop another session's claim (a stale one)")
    q = sub.add_parser("queue", help="wait in line for the stack: queue <purpose...> (as take's words)")
    q.add_argument("words", nargs="*", metavar="session purpose")
    q.add_argument("--quiet", action="store_true", help="take it as a quiet window when this session's turn comes")
    q.add_argument("--drop", action="store_true", help="leave the line (drop [session]; another's needs --force)")
    q.add_argument("--force", action="store_true")
    w = sub.add_parser("wait", help="block until the stack is free and this session is first in line, then take it")
    w.add_argument("session", nargs="?", help="default SIMSONLINE_SESSION")
    w.add_argument("--timeout", type=float, default=None, metavar="S",
                   help="exit 3 after S seconds (default: wait as long as it takes; run it in the background)")
    c = sub.add_parser("check-quiet", help="exit 3 with the reason during another session's quiet window")
    c.add_argument("--force", action="store_true", help="exit 0 anyway")
    sub.add_parser("status")
    a = p.parse_args(argv)
    now = datetime.now()
    conflict = session_conflict()
    if conflict and a.cmd in ("take", "queue", "wait", "release"):
        print(f"{a.cmd}: {conflict}")  # without it, take's first purpose word would become the session
        return 2
    if a.cmd == "check-quiet":
        return 3 if refuse_build(a.force) else 0
    if a.cmd != "wait":  # wait_turn advances the line at each look
        for note in advance(now):
            print(note)
    claim = read()
    if a.cmd == "wait":
        session = a.session or own_session()
        if not session:
            print("wait: give the session (or set SIMSONLINE_SESSION)")
            return 2
        return wait_turn(session, a.timeout)
    if a.cmd == "queue":
        if a.drop:
            mine = own_session()
            session = a.words[0] if a.words else mine
            if not session:
                print("queue --drop: give the session (or set SIMSONLINE_SESSION)")
                return 2
            if session != mine and not a.force:
                print(f"queue --drop: {session} is another session's entry; --force to drop it")
                return 3
            queue = read_queue()
            write_queue([entry for entry in queue if entry[0] != session])
            print(f"{session} left the line" if any(e[0] == session for e in queue) else f"{session} was not in line")
            return 0
        taken = take_words(a.words, own_session())
        if isinstance(taken, str):
            print(taken.replace("take", "queue"))
            return 2
        session, purpose = taken
        if a.quiet:
            purpose = quiet_purpose(purpose)
        if claim and claim[0] == session:
            print(f"{session} holds the stack already")
            return 0
        queue = read_queue()
        names = [entry[0] for entry in queue]
        if session in names:
            i = names.index(session)
            queue[i] = (session, queue[i][1], purpose or queue[i][2])
        else:
            queue.append((session, now, purpose))
        write_queue(queue)
        place = names.index(session) + 1 if session in names else len(queue)
        held = describe(claim, now) if claim else "the stack is free"
        free = "; take it now (take uses the queued purpose), or wait" if claim is None and place == 1 else ""
        print(f"{session} is {place} of {len(queue)} in line; {held}{free}")
        return 0
    if a.cmd == "take":
        taken = take_words(a.words, own_session())
        if isinstance(taken, str):
            print(taken)
            return 2
        session, purpose = taken
        if claim and claim[0] != session and not a.force:
            print(describe(claim, now) + "; not taken (wait in line: queue <purpose>, then wait; or --force if it is "
                                         "stale)")
            return 3
        queue = read_queue()
        before = ahead(session, queue)
        if claim is None and before and not a.force:
            print(f"the stack is free, but {entry_text(before[0], now)} is first in line; not taken (queue <purpose>, "
                  "then wait; or --force to go first)")
            return 3
        mine = next((entry for entry in queue if entry[0] == session), None)
        purpose = purpose or (mine[2] if mine else "")
        if a.quiet:
            purpose = quiet_purpose(purpose)
        claim_for(session, purpose, now)
        print(f"claimed for {session}" + (f": {purpose}" if purpose else ""))
        return 0
    if a.cmd == "release":
        session = a.session or own_session()
        if not session and not a.force:
            print("release: give the session (or set SIMSONLINE_SESSION)")
            return 2
        if claim is None:
            print("free already")
            return 0
        if claim[0] != session and not a.force:
            print(describe(claim, now) + f"; not released ({session} does not hold it; --force to drop it)")
            return 3
        CLAIM.unlink(missing_ok=True)
        queue = read_queue()
        if queue:
            stamp_turn(queue[0][0], now)  # its turn starts now (advance)
        print("released" + (f"; next in line: {entry_text(queue[0], now)}: tell them (their stack_claim.py wait "
                            f"takes it on its own; not taken within {TURN_MINUTES} min, it is dropped)" if queue
                            else "; nobody in line"))
        return 0
    if claim is None and CLAIM.exists():
        print(f"{CLAIM} is not a claim line (`<session> <ISO time> <purpose>`): fix or delete it")
        return 2
    print((describe(claim, now) + ended_note()) if claim else "free")
    mine = own_session()
    if claim and mine:
        print("(that is this session)" if claim[0] == mine else f"(this session is {mine})")
    for place, entry in enumerate(read_queue(), 1):
        print(f"  {place} in line: {entry_text(entry, now)}")
    for text in recent_drops(now):
        print(f"  {text}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
