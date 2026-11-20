#!/usr/bin/env python3
"""Fail the build if any banned term appears in a file name or text file under the repo.

Usage: python tools/banned_terms.py [repo_root]
Exit code 0 = clean, 1 = hits found (each printed as path:line: term).
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

SKIP_DIRS = {".git", "bin", "obj", ".vs", ".idea", "node_modules", "TestResults"}
TEXT_SUFFIXES = {
    ".cs", ".csproj", ".sln", ".props", ".targets", ".json", ".md", ".txt", ".py", ".bat", ".sh",
    ".ps1", ".xml", ".yml", ".yaml", ".glsl", ".vert", ".frag", ".ini", ".cfg", ".toml", ".gitignore",
}


def load_patterns(list_file: Path) -> list[tuple[str, re.Pattern[str]]]:
    patterns = []
    for raw in list_file.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("cs:"):
            patterns.append((line, re.compile(line[3:])))
        else:
            patterns.append((line, re.compile(line, re.IGNORECASE)))
    return patterns


def scan(root: Path) -> list[str]:
    list_file = root / "tools" / "banned_terms.txt"
    patterns = load_patterns(list_file)
    hits: list[str] = []
    for path in root.rglob("*"):
        rel = path.relative_to(root)
        if any(part in SKIP_DIRS for part in rel.parts) or path == list_file:
            continue
        for label, pat in patterns:
            if pat.search(path.name):
                hits.append(f"{rel}: file name matches {label}")
        if not path.is_file() or (path.suffix.lower() not in TEXT_SUFFIXES and path.name != ".gitignore"):
            continue
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            hits.append(f"{rel}: could not read ({exc})")
            continue
        for n, line in enumerate(text.splitlines(), 1):
            for label, pat in patterns:
                if pat.search(line):
                    hits.append(f"{rel}:{n}: matches {label}")
    return hits


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent)
    hits = scan(root)
    if hits:
        print(f"BANNED TERMS FOUND ({len(hits)}):")
        for h in hits:
            print("  " + h)
        return 1
    print("Banned-terms check: clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
