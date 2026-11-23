"""Read-only physical gameplay-fork census and shared launch guard.

python scripts/fork_census.py [--checkout PATH] [--json]
Only evidence files requested by a caller are written; profiles and receipts are never changed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

PACKAGE = "SimsOnlineGameplay02Cold.package"
TAIL = Path("Documents/Electronic Arts/The Sims 3")
ROOT = Path(__file__).resolve().parents[1]


def receipt_checkout() -> Path:
    """The same receipt root as the session brief, independent of a disposable worktree."""
    if os.environ.get("SIMSONLINE_MAIN_CHECKOUT"):
        return Path(os.environ["SIMSONLINE_MAIN_CHECKOUT"])
    import stack_claim
    return stack_claim.main_checkout()


def documents_profile() -> Path:
    """Use the deployment script's explicit override, else Windows' redirected Documents."""
    if os.environ.get("SIMSONLINE_TS3_DOCUMENTS"):
        return Path(os.environ["SIMSONLINE_TS3_DOCUMENTS"])
    if sys.platform == "win32":
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders") as key:
            documents = os.path.expandvars(winreg.QueryValueEx(key, "Personal")[0])
        return Path(documents) / "Electronic Arts/The Sims 3"
    return Path.home() / TAIL


def box_profiles(name: str) -> list[Path]:
    """Resolve every matching prefix user, including quick_loop's explicit box-profile template."""
    template = os.environ.get("SIMSONLINE_BOX_PROFILE")
    if not template:
        user = os.environ.get("USERNAME", Path.home().name)
        template = (f"{os.environ.get('SYSTEMDRIVE', 'C:')}/Sandbox/{user}/{{box}}/drive/C/Sims/"
                    ".sims3-client/drive_c/users/{user}/Documents/Electronic Arts/The Sims 3")
    if "{user}" not in template:
        return [Path(template.format(box=name))]
    users = Path(template.format(box=name, user="*").split("*")[0])
    try:
        entries = sorted(users.iterdir())
    except FileNotFoundError:
        entries = []
    profiles = [Path(template.format(box=name, user=p.name)) for p in entries
                if Path(template.format(box=name, user=p.name)).is_dir()]
    # Keep an absent target visible: it must fail a fork boot rather than disappear from the required set.
    return profiles or [Path(template.format(box=name, user=os.environ.get("SIMSONLINE_WINE_USER")
                                                                           or os.environ.get("USERNAME", Path.home().name)))]


def resolved_profiles(boxes: list[str] | None = None, wineprefixes: list[str] | None = None,
                      client_saves: str | None = None) -> tuple[list[Path], list[Path]]:
    server = documents_profile()
    profiles = [server, Path(os.environ.get("SIMSONLINE_REAL_PROFILE", str(Path.home() / TAIL)))]
    if os.environ.get("SIMSONLINE_ONEDRIVE_MOD"):
        profiles.append(Path(os.environ["SIMSONLINE_ONEDRIVE_MOD"]).parent.parent.parent)
    elif sys.platform == "win32":
        profiles.append(Path.home() / "OneDrive" / TAIL)
    required = [server] if sys.platform == "win32" or os.environ.get("SIMSONLINE_TS3_DOCUMENTS") else []
    if sys.platform == "win32":
        names = boxes if boxes is not None else [
            os.environ.get("SIMSONLINE_SANDBOXIE_BOX", "Client2Box"),
            os.environ.get("SIMSONLINE_SANDBOXIE_BOX2", "Client3Box"),
        ]
        if boxes is None:
            third = os.environ.get("SIMSONLINE_SANDBOXIE_BOX3", "Client4Box")
            if third and any((p / "Mods").is_dir() for p in box_profiles(third)):
                names.append(third)
        for name in dict.fromkeys(n for n in names if n):
            found = box_profiles(name)
            profiles.extend(found)
            required.extend(found)
    else:
        prefixes = [Path(os.environ.get("WINEPREFIX") or Path.home() / ".sims3")]
        prefixes += [Path(p) for p in wineprefixes] if wineprefixes is not None else sorted(
            Path.home().glob(".sims3-client*"))
        if wineprefixes is None and not os.environ.get("WINEPREFIX") and (Path.home() / ".wine").is_dir():
            prefixes.append(Path.home() / ".wine")
        for prefix in dict.fromkeys(prefixes):
            users = prefix / "drive_c/users"
            try:
                entries = sorted(users.iterdir())
            except FileNotFoundError:
                entries = []
            found = [p / TAIL for p in entries if (p / TAIL).is_dir()]
            if not found:
                user = os.environ.get("SIMSONLINE_WINE_USER") or os.environ.get("USER") or Path.home().name
                found = [users / user / TAIL]
            profiles.extend(found)
            required.extend(found)
    if client_saves is None:
        client_saves = os.environ.get("SIMSONLINE_CLIENT_SAVES_DIR")
    if client_saves:
        profile = Path(client_saves).parent
        profiles.append(profile)
        required.append(profile)
    return profiles, required


def census(checkout: Path, profiles: list[Path] | None = None, required: list[Path] | None = None, *,
           boxes: list[str] | None = None, wineprefixes: list[str] | None = None,
           client_saves: str | None = None) -> dict:
    """Hash physical destinations, then reconcile exact bytes with installed receipts, newest first."""
    errors = []
    if profiles is None:
        try:
            profiles, required = resolved_profiles(boxes=boxes, wineprefixes=wineprefixes, client_saves=client_saves)
        except OSError as error:
            profiles, required = [], []
            errors.append(f"profile resolution: {type(error).__name__}: {error}")
    required = profiles if required is None else required
    paths = {str((p / "Mods/Packages" / PACKAGE).resolve()) for p in profiles}
    required_paths = {str((p / "Mods/Packages" / PACKAGE).resolve()) for p in required}
    receipts = []
    try:
        files = sorted((checkout / ".omo/cache/fork").glob("cold-test*/deployment.json"),
                       key=lambda p: p.stat().st_mtime_ns, reverse=True)
        for path in files:
            try:
                record = json.loads(path.read_text(encoding="utf-8-sig"))
                if not isinstance(record, dict):
                    raise ValueError("receipt must be a JSON object")
                for field in ("preparedSha256", "originalSha256", "deploymentPackage"):
                    if field in record and not isinstance(record[field], str):
                        raise ValueError(f"{field} must be a string")
                record = dict(record, attempt=path.parent.name, receipt=str(path.resolve()))
                receipts.append(record)
                target = record.get("deploymentPackage")
                if target:
                    paths.add(str(Path(target).resolve()))
            except (OSError, ValueError) as error:
                errors.append(f"{path}: {type(error).__name__}: {error}")
    except OSError as error:
        errors.append(f"receipts: {type(error).__name__}: {error}")
    installed = [r for r in receipts if r.get("status") == "installed"]
    rows = []
    for name in sorted(paths):
        row = {"path": name, "required": name in required_paths, "present": False}
        try:
            with Path(name).open("rb") as handle:
                row.update(present=True, size=os.fstat(handle.fileno()).st_size,
                           sha256=hashlib.file_digest(handle, "sha256").hexdigest())
            matches = [r["attempt"] for r in installed
                       if r.get("preparedSha256", "").lower() == row["sha256"]]
            row["attempts"] = matches
            row["provenance"] = matches[0] if matches else "unknown provenance"
            row["stockOriginal"] = any(
                r.get("overridePackage") is False and r.get("deploymentPackage") and
                str(Path(r["deploymentPackage"]).resolve()) == name and
                r.get("originalSha256", "").lower() == row["sha256"] for r in receipts)
            if row["stockOriginal"]:
                row["provenance"] = "stock original (receipt hash verified)"
        except FileNotFoundError:
            pass
        except OSError as error:
            errors.append(f"{name}: {type(error).__name__}: {error}")
        rows.append(row)
    selected = installed[0] if installed else None
    present = [r for r in rows if r["present"] and not r.get("stockOriginal")]
    mismatches = []
    if selected:
        expected = selected.get("preparedSha256", "").lower()
        if not expected:
            errors.append(f"{selected['receipt']}: installed receipt has no preparedSha256")
        mismatches = [r["path"] for r in rows if r["required"] and
                      (not r["present"] or r.get("sha256") != expected)]
    state = ("unreadable" if errors else
             "unknown" if any(not r["attempts"] for r in present) else
             "mismatch" if mismatches or (selected and not present) else
             "fork" if selected else "stock")
    return {"observedUtc": datetime.now(timezone.utc).isoformat(), "checkout": str(checkout.resolve()),
            "state": state, "selectedAttempt": selected["attempt"] if selected else None,
            "expectedSha256": selected.get("preparedSha256") if selected else None,
            "packages": rows, "receipts": receipts, "mismatches": mismatches, "errors": errors}


def summary(result: dict) -> str:
    state = result["state"]
    if state == "stock":
        return "fork: none installed (stock; physical package census empty)"
    if state == "fork":
        return f"fork: {result['selectedAttempt']} installed (physical packages verified)"
    present = [f"{r['path']} size={r['size']} sha256={r['sha256']} ({r['provenance']})"
               for r in result["packages"] if r["present"]]
    if state == "unreadable":
        return "fork: state unreadable (" + "; ".join(result["errors"]) + "); " + "; ".join(present)
    label = "fork present, unknown provenance" if state == "unknown" else "fork profile mismatch"
    return f"fork: {label}; selected={result['selectedAttempt']}; " + "; ".join(
        present + [f"required build missing or different: {p}" for p in result["mismatches"]])


def guard(result: dict, allow_stock: bool = False) -> None:
    if result["state"] == "fork":
        return
    if result["state"] == "stock":
        print(f"FORK-MISSING: {summary(result)}. Install with quick_loop.py fork-install <attempt> "
              "(or fork-cycle --boot). Deliberate stock: --no-fork-ok or SIMSONLINE_NO_FORK_OK=1.",
              file=sys.stderr, flush=True)
        if allow_stock or os.environ.get("SIMSONLINE_NO_FORK_OK") == "1":
            return
    else:
        print(f"FORK-UNSAFE: {summary(result)}. --no-fork-ok cannot override physical forks or uncertain state.",
              file=sys.stderr, flush=True)
    raise SystemExit(2)


def write_evidence(result: dict, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkout", type=Path, default=receipt_checkout())
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--evidence", type=Path, help="write census JSON outside profiles and receipt caches")
    args = parser.parse_args()
    result = census(args.checkout)
    if args.evidence:
        write_evidence(result, args.evidence)
    print(json.dumps(result, indent=2) if args.json else summary(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
