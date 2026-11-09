"""Import docs/BACKLOG.md into a GitHub repo (milestones, labels, issues).

This is a project-management helper, not part of the WinDock product.

    python tools/create_github_project.py --dry-run
    python tools/create_github_project.py --create-repo --project-number 1 --push-local

The token is read from --token or the GITHUB_TOKEN environment variable and is
never printed or written to disk. Requires scopes: repo + project. Reruns are
idempotent (existing issues are skipped, board items are re-linked safely).
If git push cannot reach github.com, --push-local automatically falls back to
replaying local commits through the Git Data API on api.github.com.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import re
import subprocess
import sys
import time
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

    def graphql(self, query: str, variables: dict) -> dict:
        result = self.call("POST", "/graphql", {"query": query, "variables": variables})
        if result.get("errors"):
            raise RuntimeError(f"GraphQL errors: {result['errors']}")
        return result["data"]


PROJECT_QUERY = """
query($owner: String!, $number: Int!) {
  user(login: $owner) {
    projectV2(number: $number) {
      id title public
      fields(first: 30) {
        nodes {
          ... on ProjectV2Field { id name }
          ... on ProjectV2SingleSelectField { id name options { id name } }
        }
      }
    }
  }
}
"""

ADD_ITEM_MUTATION = """
mutation($projectId: ID!, $contentId: ID!) {
  addProjectV2ItemById(input: {projectId: $projectId, contentId: $contentId}) {
    item { id }
  }
}
"""

SET_STATUS_MUTATION = """
mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
  updateProjectV2ItemFieldValue(input: {
    projectId: $projectId, itemId: $itemId, fieldId: $fieldId,
    value: {singleSelectOptionId: $optionId}
  }) { projectV2Item { id } }
}
"""


def attach_to_project(gh: "GitHub", owner: str, number: int,
                     items: list[tuple[dict, str, str]]) -> None:
    """Add issues to a user-level Projects v2 board; set Status + Priority.

    Requires `read:project` + `write:project` on a classic PAT, or the
    equivalent fine-grained 'Projects' read/write permission.
    """
    data = gh.graphql(PROJECT_QUERY, {"owner": owner, "number": number})
    project = (data.get("user") or {}).get("projectV2")
    if not project:
        print(f"  ! Project #{number} not visible to this token; skipping board linking.")
        return
    print(f"  Project: {project['title']} (public={project['public']}, id={project['id']})")

    def find_field(name: str) -> dict | None:
        return next(
            (f for f in project["fields"]["nodes"]
             if f and f.get("name", "").lower() == name and f.get("options")),
            None,
        )

    status_field = find_field("status")
    priority_field = find_field("priority")

    def mutate(query: str, variables: dict) -> dict:
        # Light throttle + one backoff on GitHub secondary rate limits.
        for attempt in range(3):
            time.sleep(0.15)
            try:
                return gh.graphql(query, variables)
            except RuntimeError as exc:
                if "rate limit" in str(exc).lower() and attempt < 2:
                    time.sleep(5 * (attempt + 1))
                    continue
                raise
        raise RuntimeError("GraphQL mutation exhausted retries")

    def set_option(field: dict | None, issue_number: int, item_id: str,
                   value: str, label: str) -> None:
        if not field:
            return
        option = next((o for o in field["options"] if o["name"].lower() == value.lower()), None)
        if not option:
            return
        try:
            mutate(SET_STATUS_MUTATION, {
                "projectId": project["id"], "itemId": item_id,
                "fieldId": field["id"], "optionId": option["id"],
            })
        except RuntimeError as exc:
            print(f"  ! set {label} for #{issue_number}: {exc}")

    for issue, desired_status, priority in items:
        try:
            added = mutate(ADD_ITEM_MUTATION, {
                "projectId": project["id"], "contentId": issue["node_id"],
            })
            item_id = added["addProjectV2ItemById"]["item"]["id"]
        except RuntimeError as exc:
            if "already" in str(exc).lower():
                continue
            print(f"  ! add item for #{issue['number']}: {exc}")
            continue
        set_option(status_field, issue["number"], item_id, desired_status, "Status")
        set_option(priority_field, issue["number"], item_id, priority, "Priority")
        print(f"  + board item #{issue['number']} -> {desired_status} / {priority}")


def push_local_repo(token: str, owner: str, repo: str) -> None:
    """Push the local git repository without persisting the token in .git/config."""
    url = f"https://github.com/{owner}/{repo}.git"
    check = subprocess.run(["git", "-C", str(ROOT), "remote", "get-url", "origin"],
                           capture_output=True, text=True)
    action = "set-url" if check.returncode == 0 else "add"
    subprocess.run(["git", "-C", str(ROOT), "remote", action, "origin", url], check=True)
    env = os.environ.copy()
    # Pass credentials via environment-scoped git config: no token in argv or .git/config.
    env["GIT_CONFIG_COUNT"] = "1"
    env["GIT_CONFIG_KEY_0"] = "http.extraHeader"
    env["GIT_CONFIG_VALUE_0"] = f"AUTHORIZATION: bearer {token}"
    env["GIT_TERMINAL_PROMPT"] = "0"
    env["LC_ALL"] = "C"  # keep git messages ASCII; Windows git may emit GBK text
    result = subprocess.run(
        ["git", "-C", str(ROOT), "push", "-u", "origin", "HEAD:main"],
        capture_output=True, text=True, env=env, timeout=180,
        encoding="utf-8", errors="replace",
    )
    out = (result.stdout or "").strip()
    err = (result.stderr or "").strip()
    if out:
        print(out)
    if result.returncode != 0:
        raise RuntimeError(f"git push failed: {err[:500]}")


def _git_bytes(*args: str) -> bytes:
    return subprocess.check_output(["git", "-C", str(ROOT), *args])


def _git_text(*args: str) -> str:
    return subprocess.check_output(
        ["git", "-C", str(ROOT), *args], text=True,
        encoding="utf-8", errors="replace",
    )


def push_via_api(gh: "GitHub", owner: str, repo: str) -> None:
    """Replay local commits through the Git Data API when git push cannot reach github.com.

    Some sandboxed networks proxy only api.github.com (git smart-HTTP on
    github.com returns 502). This path keeps the full local commit history by
    recreating each tree/commit server-side, using the same REST channel that
    already works for issues and milestones.
    """
    base = f"/repos/{owner}/{repo}"
    local_shas = _git_text("log", "--reverse", "--format=%H", "HEAD").split()
    if not local_shas:
        raise RuntimeError("no local commits to push")

    remote_head = None
    try:
        remote_head = gh.call("GET", f"{base}/git/ref/heads/main")["object"]["sha"]
    except RuntimeError as exc:
        if "HTTP 404" not in str(exc) and "HTTP 409" not in str(exc):
            raise
    if remote_head:
        remote_msg = gh.call("GET", f"{base}/git/commits/{remote_head}")["message"].strip()
        local_msg = _git_text("log", "-1", "--format=%B", "HEAD").strip()
        if remote_msg == local_msg:
            print("  remote main already matches local HEAD; nothing to push")
            return
        raise RuntimeError("remote main has different history; refusing to overwrite it")

    parent = None
    for sha in local_shas:
        message = _git_text("log", "-1", "--format=%B", sha).strip()
        tree_items = []
        for record in _git_bytes("ls-tree", "-r", "-z", sha).split(b"\0"):
            if not record:
                continue
            meta, path = record.split(b"\t", 1)
            mode, obj_type, obj_sha = meta.split(b" ", 2)
            if obj_type != b"blob":
                continue
            content = _git_bytes("cat-file", "blob", obj_sha.decode("ascii"))
            blob = gh.call("POST", f"{base}/git/blobs", {
                "content": base64.b64encode(content).decode("ascii"),
                "encoding": "base64",
            })
            tree_items.append({
                "path": path.decode("utf-8"), "mode": mode.decode("ascii"),
                "type": "blob", "sha": blob["sha"],
            })
        tree = gh.call("POST", f"{base}/git/trees", {"tree": tree_items})
        payload = {"message": message, "tree": tree["sha"]}
        if parent:
            payload["parents"] = [parent]
        parent = gh.call("POST", f"{base}/git/commits", payload)["sha"]
        print(f"  + api commit {parent[:8]} {message.splitlines()[0]}")

    gh.call("POST", f"{base}/git/refs", {"ref": "refs/heads/main", "sha": parent})
    print(f"  + created refs/heads/main at {parent[:8]}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--token", default=os.environ.get("GITHUB_TOKEN"))
    ap.add_argument("--owner", default="boom1g996")
    ap.add_argument("--repo", default="WinDock")
    ap.add_argument("--create-repo", action="store_true", help="create the repository if missing")
    ap.add_argument("--private", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--project-owner", default="boom1g996",
                    help="owner of the user-level Projects v2 board")
    ap.add_argument("--project-number", type=int, default=0,
                    help="Projects v2 number to link issues into; 0 = skip board linking")
    ap.add_argument("--push-local", action="store_true",
                    help="also push the local git repository to origin/main (falls back to API)")
    ap.add_argument("--push-api", action="store_true",
                    help="skip git push and upload commits via the Git Data API only")
    args = ap.parse_args()

    milestones, issues = parse_backlog()
    print(f"Parsed: {len(milestones)} milestones, {len(issues)} issues, {len(LABELS)} labels")
    print(f"Plan: repo={args.owner}/{args.repo}, board=#{args.project_number or 'off'}, "
          f"push_local={args.push_local}")
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

    existing: dict[str, dict] = {}
    page = 1
    while True:
        batch = gh.call("GET", f"{base}/issues?state=all&per_page=100&page={page}")
        if not batch:
            break
        existing.update({x["title"]: x for x in batch})
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
    board_items: list[tuple[dict, str, str]] = []
    for i in issues:
        target = existing.get(i["title"])
        if target is None:
            labels = list(dict.fromkeys(i["labels"] + [i["priority"]]))
            payload = {
                "title": i["title"],
                "body": body_template.format(**i),
                "labels": labels,
            }
            number = milestone_numbers.get(i["milestone"])
            if number:
                payload["milestone"] = number
            target = gh.call("POST", f"{base}/issues", payload)
            if i["state"] == "done":
                gh.call("PATCH", f"{base}/issues/{target['number']}", {"state": "closed"})
            created_count += 1
            print(f"  + issue #{target['number']} {i['title']} ({i['state']})")
        board_items.append((target, "Done" if i["state"] == "done" else "Todo", i["priority"]))

    print(f"Done. Created {created_count} new issues in {owner}/{repo}.")
    if args.project_number:
        try:
            attach_to_project(gh, args.project_owner, args.project_number, board_items)
        except RuntimeError as exc:
            print(f"  ! board linking failed (issues were still created): {exc}")
    if args.push_local or args.push_api:
        pushed = False
        if args.push_local:
            try:
                push_local_repo(args.token, owner, repo)
                pushed = True
                print("  + pushed local repository to origin/main (git)")
            except (RuntimeError, subprocess.SubprocessError) as exc:
                print(f"  ! git push failed: {exc}; falling back to Git Data API")
        if not pushed:
            try:
                push_via_api(gh, owner, repo)
                print("  + pushed local repository to origin/main (Git Data API)")
            except (RuntimeError, subprocess.SubprocessError) as exc:
                print(f"  ! API push failed (issues were still created): {exc}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
