#!/usr/bin/env python3
"""Release notes для vlc-remote из описаний pull request'ов.

Правила оформления PR — в CLAUDE.md. Кратко: заголовок `<тип>(<область>): <описание>`,
в теле секция `## Release notes` со списком пунктов для пользователя или словом «нет».

Команды:
  check                      проверить заголовок и тело PR (env PR_TITLE, PR_BODY); код 1 при ошибке
  beta   --sha SHA           заметки для тестовой сборки: PR, из которого получен коммит
  stable [--since-tag TAG]   заметки для релиза: все PR, смёрдженные в main после предыдущего релиза

Для beta/stable нужны gh CLI и GH_TOKEN; репозиторий берётся из GITHUB_REPOSITORY или --repo.
Результат (Markdown) печатается в stdout.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys

TYPES = ("feat", "fix", "perf", "refactor", "docs", "test", "build", "ci", "chore", "revert")
SCOPES = ("android", "companion", "protocol", "lua", "deploy", "docs", "ci", "deps")
TITLE_RE = re.compile(
    r"^(?P<type>" + "|".join(TYPES) + r")"
    r"(?:\((?P<scope>[a-z0-9-]+(?:,[a-z0-9-]+)*)\))?"
    r"(?P<breaking>!)?: (?P<subject>\S.*)$"
)
NOTES_HEADING_RE = re.compile(r"^##\s+release\s+notes\s*$", re.IGNORECASE | re.MULTILINE)
EMPTY_MARKERS = {"нет", "-", "—", "none", "n/a"}

SECTIONS = [  # (заголовок в релизе, предикат по типу/breaking)
    ("⚠️ Важно", lambda t, b: b),
    ("Новое", lambda t, b: t == "feat"),
    ("Исправления", lambda t, b: t == "fix"),
    ("Улучшения", lambda t, b: t in ("perf", "refactor")),
    ("Прочее", lambda t, b: True),
]


def extract_notes(body: str | None) -> list[str] | None:
    """Пункты из секции `## Release notes`. None — секции нет; [] — явно «нет»."""
    if not body:
        return None
    body = re.sub(r"<!--.*?-->", "", body, flags=re.DOTALL).replace("\r\n", "\n")
    m = NOTES_HEADING_RE.search(body)
    if not m:
        return None
    rest = body[m.end():]
    nxt = re.search(r"^##\s", rest, re.MULTILINE)
    section = rest[: nxt.start()] if nxt else rest
    lines = [ln.rstrip() for ln in section.strip().split("\n") if ln.strip()]
    if not lines or (len(lines) == 1 and lines[0].strip(" -*").lower() in EMPTY_MARKERS):
        return []
    items: list[str] = []
    for ln in lines:
        s = ln.strip()
        if s.startswith(("- ", "* ")):
            items.append(s[2:].strip())
        elif items and ln.startswith((" ", "\t")):
            items[-1] += " " + s  # перенос строки внутри пункта
        else:
            items.append(s)
    return [i for i in items if i and i.lower() not in EMPTY_MARKERS]


def parse_title(title: str) -> tuple[str, bool] | None:
    m = TITLE_RE.match(title.strip())
    if not m:
        return None
    return m.group("type"), bool(m.group("breaking"))


def cmd_check() -> int:
    title = os.environ.get("PR_TITLE", "")
    body = os.environ.get("PR_BODY", "")
    errors = []
    m = TITLE_RE.match(title.strip())
    if not m:
        errors.append(
            f"Заголовок «{title}» не по формату `<тип>(<область>): <описание>`. "
            f"Типы: {', '.join(TYPES)}. Области: {', '.join(SCOPES)}."
        )
    else:
        bad = [s for s in (m.group("scope") or "").split(",") if s and s not in SCOPES]
        if bad:
            errors.append(f"Неизвестная область: {', '.join(bad)}. Допустимые: {', '.join(SCOPES)}.")
    notes = extract_notes(body)
    if notes is None:
        errors.append("В описании нет секции `## Release notes` (пункты для пользователя или «нет»).")
    elif m and m.group("type") in ("feat", "fix") and not notes:
        errors.append("Для feat/fix секция `## Release notes` не может быть пустой: опишите изменение для пользователя.")
    for e in errors:
        print(f"::error::{e}")
    if not errors:
        print("PR оформлен правильно. Release notes:")
        print("\n".join(f"- {n}" for n in notes) if notes else "(нет — изменение не попадёт в заметки)")
    return 1 if errors else 0


def gh_json(args: list[str]):
    out = subprocess.run(["gh", *args], check=True, capture_output=True, text=True, encoding="utf-8").stdout
    return json.loads(out) if out.strip() else None


def render(prs: list[dict]) -> str:
    buckets: dict[str, list[str]] = {name: [] for name, _ in SECTIONS}
    for pr in prs:
        parsed = parse_title(pr.get("title", "")) or ("chore", False)
        notes = extract_notes(pr.get("body"))
        if not notes:
            continue
        for name, pred in SECTIONS:
            if pred(*parsed):
                buckets[name].extend(f"{n} (#{pr['number']})" for n in notes)
                break
    parts = [f"## {name}\n" + "\n".join(f"- {i}" for i in items) for name, items in buckets.items() if items]
    return "\n\n".join(parts) if parts else "Изменений, заметных пользователю, нет."


def cmd_beta(repo: str, sha: str) -> int:
    prs = gh_json(["api", f"repos/{repo}/commits/{sha}/pulls"]) or []
    prs = [p for p in prs if p.get("merged_at")]
    if not prs:
        subject = subprocess.run(["git", "log", "-1", "--format=%s", sha], capture_output=True, text=True,
                                 encoding="utf-8").stdout.strip()
        print(f"Сборка из коммита без pull request: {subject}")
        return 0
    print(render([{"number": p["number"], "title": p["title"], "body": p.get("body")} for p in prs[:1]]))
    return 0


def cmd_stable(repo: str, since_tag: str | None) -> int:
    since = None
    if since_tag:
        rel = gh_json(["release", "view", since_tag, "--repo", repo, "--json", "publishedAt"])
        since = rel["publishedAt"] if rel else None
    query = ["pr", "list", "--repo", repo, "--state", "merged", "--base", "main", "--limit", "300",
             "--json", "number,title,body,mergedAt"]
    if since:
        query += ["--search", f"merged:>{since}"]
    prs = sorted(gh_json(query) or [], key=lambda p: p["mergedAt"])
    print(render(prs))
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["check", "beta", "stable"])
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"))
    ap.add_argument("--sha")
    ap.add_argument("--since-tag")
    a = ap.parse_args()
    if a.command == "check":
        return cmd_check()
    if not a.repo:
        ap.error("--repo или GITHUB_REPOSITORY обязателен")
    if a.command == "beta":
        if not a.sha:
            ap.error("--sha обязателен для beta")
        return cmd_beta(a.repo, a.sha)
    return cmd_stable(a.repo, a.since_tag)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
