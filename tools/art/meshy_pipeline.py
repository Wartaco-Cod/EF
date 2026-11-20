#!/usr/bin/env python3
"""Meshy pipeline: text prompt -> textured 3D model -> rig -> animations, downloaded as GLB files.

Then render them with the locked Blender rig (blender/templar_render_rig.py) and pack with pack_sheet.py.

Set your key first:   set MESHY_API_KEY=msy_...      (Windows)   export MESHY_API_KEY=msy_...   (Linux)

Examples
  python tools/art/meshy_pipeline.py library --search attack
  python tools/art/meshy_pipeline.py create royal_sergeant --prompt-file tools/art/prompts/royal_sergeant.txt
  python tools/art/meshy_pipeline.py create royal_sergeant --prompt "..." --actions idle,walk,attack,hit,death
  python tools/art/meshy_pipeline.py status royal_sergeant

Every step's task id is saved in art_source/<name>/manifest.json, so a stopped run resumes where it left off
and never pays twice for the same step. Creating tasks needs a paid Meshy plan.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

API = "https://api.meshy.ai"
ROOT = Path(__file__).resolve().parents[2]
SOURCE_DIR = ROOT / "art_source"
STYLE_SUFFIX = (
    "painterly realistic, 14th-century France, muted earth palette, worn and weathered materials, "
    "full body, standing in A-pose, single character, no base, no text"
)


def key() -> str:
    k = os.environ.get("MESHY_API_KEY", "").strip()
    if not k:
        sys.exit("MESHY_API_KEY is not set. Get a key at meshy.ai (Settings > API) and set it first.")
    return k


def call(method: str, path: str, body: dict | None = None) -> dict:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {key()}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    for attempt in range(5):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read() or b"{}")
        except urllib.error.HTTPError as e:
            detail = e.read().decode(errors="replace")[:400]
            if e.code == 429 and attempt < 4:
                time.sleep(10 * (attempt + 1))
                continue
            if e.code == 402:
                sys.exit("Meshy says 402: not enough credits, or the plan doesn't allow API tasks.")
            sys.exit(f"Meshy {method} {path} failed: HTTP {e.code} {detail}")
    sys.exit("Meshy kept rate-limiting; try again later.")


def wait(path: str, label: str) -> dict:
    while True:
        t = call("GET", path)
        status, progress = t.get("status"), t.get("progress", 0)
        print(f"  {label}: {status} {progress}%", end="\r", flush=True)
        if status == "SUCCEEDED":
            print(f"  {label}: done ({t.get('consumed_credits', '?')} credits)      ")
            return t
        if status in ("FAILED", "CANCELED"):
            print()
            sys.exit(f"{label} {status}: {t.get('task_error')}")
        time.sleep(5)


def download(url: str, dest: Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url, timeout=300) as r, open(dest, "wb") as f:
        f.write(r.read())
    print(f"  saved {dest.relative_to(ROOT)}")


def load_manifest(name: str) -> tuple[Path, dict]:
    d = SOURCE_DIR / name
    d.mkdir(parents=True, exist_ok=True)
    m = d / "manifest.json"
    return m, (json.loads(m.read_text()) if m.exists() else {"name": name})


def save_manifest(path: Path, manifest: dict) -> None:
    path.write_text(json.dumps(manifest, indent=2))


def library(search: str | None) -> list[dict]:
    q = f"?search={urllib.request.quote(search)}" if search else ""
    res = call("GET", f"/openapi/v1/animations/library{q}")
    items = res if isinstance(res, list) else res.get("result") or res.get("items") or res.get("data") or []
    return items


def pick_action(word: str) -> int | None:
    """Finds a preset animation id for a word like 'attack' or 'death'. Prefers the shortest matching name."""
    items = library(word)
    best = None
    for it in items:
        aid = it.get("action_id", it.get("id"))
        name = str(it.get("name", it.get("key", "")))
        if aid is None:
            continue
        if best is None or len(name) < len(best[1]):
            best = (int(aid), name)
    if best:
        print(f"  action '{word}' -> {best[0]} ({best[1]})")
        return best[0]
    print(f"  no preset animation found for '{word}'; skipping")
    return None


def create(args: argparse.Namespace) -> None:
    prompt = Path(args.prompt_file).read_text().strip() if args.prompt_file else args.prompt
    if not prompt:
        sys.exit("Give --prompt or --prompt-file.")
    full_prompt = f"{prompt}, {STYLE_SUFFIX}"[:800]
    mpath, m = load_manifest(args.name)
    out = SOURCE_DIR / args.name

    if "preview_id" not in m:
        body = {"mode": "preview", "prompt": full_prompt, "ai_model": args.model, "should_remesh": True,
                "topology": "triangle", "target_polycount": args.polycount}
        m["preview_id"] = call("POST", "/openapi/v2/text-to-3d", body)["result"]
        m["prompt"] = full_prompt
        save_manifest(mpath, m)
    wait(f"/openapi/v2/text-to-3d/{m['preview_id']}", "model")

    if "refine_id" not in m:
        body = {"mode": "refine", "preview_task_id": m["preview_id"], "enable_pbr": False,
                "texture_prompt": (args.texture_prompt or prompt)[:800], "texture_resolution": "2k"}
        m["refine_id"] = call("POST", "/openapi/v2/text-to-3d", body)["result"]
        save_manifest(mpath, m)
    refined = wait(f"/openapi/v2/text-to-3d/{m['refine_id']}", "texture")
    if glb := refined.get("model_urls", {}).get("glb"):
        download(glb, out / f"{args.name}_textured.glb")

    if args.no_rig:
        return
    if "rig_id" not in m:
        m["rig_id"] = call("POST", "/openapi/v1/rigging", {"input_task_id": m["refine_id"], "height_meters": args.height})["result"]
        save_manifest(mpath, m)
    rig = wait(f"/openapi/v1/rigging/{m['rig_id']}", "rig")
    res = rig.get("result") or {}
    if res.get("rigged_character_glb_url"):
        download(res["rigged_character_glb_url"], out / f"{args.name}_rigged.glb")

    m.setdefault("animations", {})
    for word in [a.strip() for a in args.actions.split(",") if a.strip()]:
        if word not in m["animations"]:
            aid = pick_action(word)
            if aid is None:
                continue
            m["animations"][word] = {"action_id": aid,
                                     "task_id": call("POST", "/openapi/v1/animations", {"rig_task_id": m["rig_id"], "action_id": aid})["result"]}
            save_manifest(mpath, m)
        anim = wait(f"/openapi/v1/animations/{m['animations'][word]['task_id']}", f"anim {word}")
        if url := (anim.get("result") or {}).get("animation_glb_url"):
            download(url, out / f"{args.name}_{word}.glb")
    print(f"\nDone. Next: open {args.name}_rigged.glb in Blender, add the animations, run templar_render_rig.py,")
    print(f"then: python tools/art/pack_sheet.py <render folder>/{args.name}")


def status(args: argparse.Namespace) -> None:
    _, m = load_manifest(args.name)
    print(json.dumps(m, indent=2))


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("create", help="model, texture, rig and animate one character")
    c.add_argument("name", help="asset name, e.g. royal_sergeant (becomes mon_royal_sergeant in game)")
    c.add_argument("--prompt")
    c.add_argument("--prompt-file")
    c.add_argument("--texture-prompt")
    c.add_argument("--model", default="latest")
    c.add_argument("--polycount", type=int, default=30000)
    c.add_argument("--height", type=float, default=1.8, help="character height in metres for rigging")
    c.add_argument("--actions", default="idle,walk,attack,hit,death")
    c.add_argument("--no-rig", action="store_true", help="props and buildings: stop after the textured model")
    c.set_defaults(fn=create)
    l = sub.add_parser("library", help="list Meshy preset animations")
    l.add_argument("--search")
    l.set_defaults(fn=lambda a: [print(json.dumps(i)) for i in library(a.search)])
    s = sub.add_parser("status", help="show saved task ids for an asset")
    s.add_argument("name")
    s.set_defaults(fn=status)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
