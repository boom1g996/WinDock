"""Import docs/BACKLOG.md into a GitHub repo (milestones, labels, issues).

This is a project-management helper, not part of the WinDock product.

    python tools/create_github_project.py --dry-run
    python tools/create_github_project.py --token <PAT> --repo WinDock --create-repo

The token is read from --token or the GITHUB_TOKEN environment variable and is
never printed or written to disk. Requires scopes: repo (public_repo is enough
for a public repo). Project v2 board columns are managed in the GitHub UI;
this script creates the issues that you then add to the board.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BACKLOG = ROOT / "docs" / "BACKLOG.md"
API = "https://api.github.com"
LABELS = [
    "P0", "P1", "P2",
    "module:taskbar", "module:window", "module:icon", "module:renderer",
    "module:animation", "module:preview", "module:config", "module:settings",
    "module:watchdog",
    "type:research", "type:feature", "type:chore", "type:test",
    "risk:system-api",
]
LABEL_COLORS = {
    "P0": "B60205", "P1": "D93F0B", "P2": "FBCA04",
    "risk:system-api": "5319E7",
    "type:test": "1D76DB", "type:research": "C2E0C6",
}


def table_rows(section: str) -> list[list[str]]:
    """Extract the first markdown table under a '## N. <section>' heading."""
    text = BACKLOG.read_text(encoding="utf-8")
    match = re.search(rf"^## .*{re.escape(section)}.*$", text, re.M)
    if not match:
        raise ValueError(f"section not found: {section}")
    rows: list[list[str]] = []
    for line in text[match.end():].splitlines():
        stripped = line.strip()
        if stripped.startswith("## ") and rows:
            break
        if not stripped.startswith("|"):
            continue
        cells = [c.strip() for c in stripped.strip("|").split("|")]
        if all(re.fullmatch(r":?-{2,}:?", c or "-") for c in cells):
            continue
        rows.append(cells)
    return rows[1:]  # drop header


def parse_backlog() -> tuple[list[dict], list[dict]]:
    milestone_rows = table_rows("Milestones")
    milestones = [{"title": r[0], "description": f"范围：{r[1]}\n出口标准：{r[2]}"}
                  for r in milestone_rows if len(r) >= 3]
    issues = []
    for r in table_rows("Issues"):
        if len(r) < 8 or not re.fullmatch(r"WD-[\w-]+", r[0]):
            continue
        issues.append({
            "id": r[0], "title": f"{r[0]} {r[1]}", "milestone": r[2],
            "priority": r[3],
            "labels": [x.strip() for x in r[4].split(",") if x.strip()],
            "depends": r[5], "acceptance": r[6], "state": r[7].strip().lower(),
        })
    return milestones, issues


class GitHub:
    def __init__(self, token: str):
        self.token = token

    def call(self, method: str, path: str, payload: dict | None = None):
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8") if payload is not None else None
        req = urllib.request.Request(
            API + path, data=data, method=method,
            headers={
                "Authorization": f"Bearer {self.token}",
                "Accept": "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28",
                "Content-Type": "application/json; charset=utf-8",
                "User-Agent": "WinDock-Backlog-Importer",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                raw = resp.read()
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", "replace")
            raise RuntimeError(f"{method} {path} -> HTTP {exc.code}: {body[:400]}") from exc


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--token", default=os.environ.get("GITHUB_TOKEN"))
    ap.add_argument("--owner", default="boom1g996")
    ap.add_argument("--repo", default="WinDock")
    ap.add_argument("--create-repo", action="store_true", help="create the repository if missing")
    ap.add_argument("--private", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    milestones, issues = parse_backlog()
    print(f"Parsed: {len(milestones)} milestones, {len(issues)} issues, {len(LABELS)} labels")
    if args.dry_run:
        for m in milestones:
            print(f"  MILESTONE  {m['title']}")
        for i in issues[:5]:
            print(f"  ISSUE      {i['title']} | {i['milestone']} | {','.join(i['labels'])} | {i['state']}")
        print(f"  ... and {max(0, len(issues) - 5)} more issues")
        print("Dry run complete. Nothing was sent to GitHub.")
        return 0

    if not args.token:
        print("ERROR: pass --token or set GITHUB_TOKEN", file=sys.stderr)
        return 2

    gh = GitHub(args.token)
    owner, repo = args.owner, args.repo
    base = f"/repos/{owner}/{repo}"

    try:
        gh.call("GET", base)
        print(f"Repository {owner}/{repo} exists.")
    except RuntimeError as exc:
        if not args.create_repo or "HTTP 404" not in str(exc):
            raise
        gh.call("POST", "/user/repos", {
            "name": repo, "private": args.private,
            "description": "Windows 11 任务栏 macOS Dock 化工具",
            "has_issues": True, "has_projects": True,
        })
        print(f"Created repository {owner}/{repo}.")

    existing_titles = set()
    page = 1
    while True:
        batch = gh.call("GET", f"{base}/issues?state=all&per_page=100&page={page}")
        if not batch:
            break
        existing_titles.update(x["title"] for x in batch)
        if len(batch) < 100:
            break
        page += 1

    for label in LABELS:
        try:
            gh.call("POST", f"{base}/labels", {
                "name": label, "color": LABEL_COLORS.get(label, "EDEDED"),
            })
            print(f"  + label {label}")
        except RuntimeError as exc:
            if "already_exists" not in str(exc):
                print(f"  ! label {label}: {exc}")

    milestone_numbers: dict[str, int] = {}
    for m in milestones:
        try:
            created = gh.call("POST", f"{base}/milestones", m)
            milestone_numbers[m["title"]] = created["number"]
            print(f"  + milestone {m['title']}")
        except RuntimeError as exc:
            if "already_exists" in str(exc):
                found = [x for x in gh.call("GET", f"{base}/milestones?state=all&per_page=100")
                         if x["title"] == m["title"]]
                milestone_numbers[m["title"]] = found[0]["number"] if found else 0
            else:
                print(f"  ! milestone {m['title']}: {exc}")

    body_template = (
        "## 依赖\n{depends}\n\n"
        "## 验收 / 出口标准\n{acceptance}\n\n"
        "## 来源\n"
        "- `需求文档.md` v1.0\n"
        "- `docs/M0-技术预研报告.md`\n"
        "- `docs/M1-设计说明与任务拆解.md`\n"
    )
    created_count = 0
    for i in issues:
        if i["title"] in existing_titles:
            continue
        labels = list(dict.fromkeys(i["labels"] + [i["priority"]]))
        payload = {
            "title": i["title"],
            "body": body_template.format(**i),
            "labels": labels,
        }
        number = milestone_numbers.get(i["milestone"])
        if number:
            payload["milestone"] = number
        result = gh.call("POST", f"{base}/issues", payload)
        if i["state"] == "done":
            gh.call("PATCH", f"{base}/issues/{result['number']}", {"state": "closed"})
        created_count += 1
        print(f"  + issue #{result['number']} {i['title']} ({i['state']})")

    print(f"Done. Created {created_count} new issues in {owner}/{repo}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
