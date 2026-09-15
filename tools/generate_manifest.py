#!/usr/bin/env python3
"""
Генератор манифеста модов для WhiteMC.

Сканирует папку mods/, для каждого jar считает SHA-512 и MurmurHash2
(CurseForge fingerprint), затем через API Modrinth и CurseForge
определяет источник. Пишет manifest.json и unresolved.json.

Использование:
    export CURSEFORGE_API_KEY="..."
    python generate_manifest.py /home/aut1st1c/.whitemc/instances/IndustrialAdventure/mods/ \
        --output manifest.json \
        --unresolved unresolved.json \
        --archive-url https://github.com/.../mods.zip
"""

import argparse
import hashlib
import json
import os
import sys
import time
from pathlib import Path
from typing import Optional
from urllib.request import Request, urlopen
from urllib.error import HTTPError

MODRINTH_API = "https://api.modrinth.com/v2"
CURSEFORGE_API = "https://api.curseforge.com/v1"
USER_AGENT = "WhiteMC-ManifestGenerator/1.0 (github.com/aut1st1c/WhiteMC)"
CHUNK_MODRINTH = 100
CHUNK_CURSEFORGE = 1000


# ------------------------------------------------------------------ #
#  Хеши
# ------------------------------------------------------------------ #

def sha512_file(path: Path) -> str:
    h = hashlib.sha512()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().lower()


def sha1_file(path: Path) -> str:
    h = hashlib.sha1()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().lower()


# ------------------------------------------------------------------ #
#  CurseForge fingerprint = MurmurHash2 без пробельных байт
# ------------------------------------------------------------------ #

_WHITESPACE = (0x09, 0x0A, 0x0D, 0x20)


def _murmur2(data: bytes, seed: int = 1) -> int:
    m = 0x5BD1E995
    r = 24
    length = len(data)
    h = (seed ^ length) & 0xFFFFFFFF
    i = 0

    while length >= 4:
        k = (data[i] | (data[i + 1] << 8)
             | (data[i + 2] << 16) | (data[i + 3] << 24)) & 0xFFFFFFFF
        k = (k * m) & 0xFFFFFFFF
        k ^= k >> r
        k = (k * m) & 0xFFFFFFFF

        h = (h * m) & 0xFFFFFFFF
        h ^= k

        i += 4
        length -= 4

    if length == 3:
        h ^= (data[i + 2] << 16) & 0xFFFFFFFF
    if length >= 2:
        h ^= (data[i + 1] << 8) & 0xFFFFFFFF
    if length >= 1:
        h ^= data[i]
        h = (h * m) & 0xFFFFFFFF

    h ^= h >> 13
    h = (h * m) & 0xFFFFFFFF
    h ^= h >> 15

    return h & 0xFFFFFFFF


def curseforge_fingerprint(path: Path) -> int:
    with path.open("rb") as f:
        data = f.read()
    filtered = bytes(b for b in data if b not in _WHITESPACE)
    return _murmur2(filtered)


# ------------------------------------------------------------------ #
#  HTTP
# ------------------------------------------------------------------ #

def http_post_json(url: str, body: dict, headers: Optional[dict] = None) -> dict:
    data = json.dumps(body).encode("utf-8")
    req = Request(url, data=data, method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("User-Agent", USER_AGENT)
    if headers:
        for k, v in headers.items():
            req.add_header(k, v)

    with urlopen(req, timeout=60) as resp:
        return json.loads(resp.read().decode("utf-8"))


# ------------------------------------------------------------------ #
#  Modrinth
# ------------------------------------------------------------------ #

def lookup_modrinth(hashes: list) -> dict:
    """sha512 -> {project_id, version_id, url, sha1, size}"""
    result = {}
    for i in range(0, len(hashes), CHUNK_MODRINTH):
        chunk = hashes[i:i + CHUNK_MODRINTH]
        try:
            resp = http_post_json(
                f"{MODRINTH_API}/version_files",
                {"hashes": chunk, "algorithm": "sha512"},
            )
        except HTTPError as e:
            print(f"[Modrinth] HTTP {e.code}: {e.reason}", file=sys.stderr)
            continue

        for h, version in resp.items():
            files = version.get("files") or []
            if not files:
                continue
            primary = next((f for f in files if f.get("primary")), files[0])
            result[h.lower()] = {
                "project_id": version.get("project_id"),
                "version_id": version.get("id"),
                "url": primary.get("url"),
                "size": primary.get("size"),
                "sha1": (primary.get("hashes") or {}).get("sha1"),
            }

        if i + CHUNK_MODRINTH < len(hashes):
            time.sleep(0.3)

    return result


# ------------------------------------------------------------------ #
#  CurseForge
# ------------------------------------------------------------------ #

def lookup_curseforge(fingerprints: list, api_key: str) -> dict:
    """fingerprint -> {mod_id, file_id}"""
    result = {}
    for i in range(0, len(fingerprints), CHUNK_CURSEFORGE):
        chunk = fingerprints[i:i + CHUNK_CURSEFORGE]
        try:
            resp = http_post_json(
                f"{CURSEFORGE_API}/fingerprints",
                {"fingerprints": chunk},
                headers={"x-api-key": api_key},
            )
        except HTTPError as e:
            print(f"[CurseForge] HTTP {e.code}: {e.reason}", file=sys.stderr)
            continue

        matches = (resp.get("data") or {}).get("exactMatches") or []
        for m in matches:
            fp = m.get("id")
            file_obj = m.get("file") or {}
            mod_id = file_obj.get("modId")
            file_id = file_obj.get("id")
            if fp is not None and mod_id and file_id:
                result[int(fp)] = {"mod_id": mod_id, "file_id": file_id}

        if i + CHUNK_CURSEFORGE < len(fingerprints):
            time.sleep(0.5)

    return result


# ------------------------------------------------------------------ #
#  Main
# ------------------------------------------------------------------ #

def main():
    ap = argparse.ArgumentParser(description="Генератор манифеста модов WhiteMC")
    ap.add_argument("mods_dir", type=Path)
    ap.add_argument("--output", type=Path, default=Path("manifest.json"))
    ap.add_argument("--unresolved", type=Path, default=Path("unresolved.json"))
    ap.add_argument("--archive-url", type=str, default=None,
                    help="URL архива mods.zip (fallback для неопознанных)")
    ap.add_argument("--manifest-version", type=str, default=None)
    ap.add_argument("--no-curseforge", action="store_true")
    args = ap.parse_args()

    if not args.mods_dir.is_dir():
        print(f"Папка не найдена: {args.mods_dir}", file=sys.stderr)
        sys.exit(1)

    jars = sorted(p for p in args.mods_dir.iterdir()
                  if p.is_file() and p.suffix.lower() == ".jar")

    if not jars:
        print("В папке нет .jar файлов", file=sys.stderr)
        sys.exit(1)

    print(f"Найдено {len(jars)} jar-файлов")

    # 1) Хеши
    print("Хеширование…")
    entries = []
    for jar in jars:
        entries.append({
            "path": jar,
            "filename": jar.name,
            "size": jar.stat().st_size,
            "sha512": sha512_file(jar),
            "sha1": sha1_file(jar),
        })

    # 2) Modrinth
    print("Поиск в Modrinth…")
    modrinth = lookup_modrinth([e["sha512"] for e in entries])
    print(f"  Modrinth: опознано {len(modrinth)} из {len(entries)}")

    # 3) CurseForge — только для тех, кого не нашёл Modrinth
    curseforge = {}
    if not args.no_curseforge:
        api_key = os.environ.get("CURSEFORGE_API_KEY")
        if not api_key:
            print("[CurseForge] CURSEFORGE_API_KEY не задан, пропускаю",
                  file=sys.stderr)
        else:
            remaining = [e for e in entries if e["sha512"] not in modrinth]
            print(f"Поиск в CurseForge ({len(remaining)} модов)…")

            fps = []
            for e in remaining:
                try:
                    fp = curseforge_fingerprint(e["path"])
                    e["fingerprint"] = fp
                    fps.append(fp)
                except Exception as ex:
                    print(f"  fingerprint error for {e['filename']}: {ex}",
                          file=sys.stderr)

            if fps:
                curseforge = lookup_curseforge(fps, api_key)
                print(f"  CurseForge: опознано {len(curseforge)}")

    # 4) Манифест
    manifest_mods = []
    unresolved = []

    for e in entries:
        sha = e["sha512"]

        if sha in modrinth:
            info = modrinth[sha]
            manifest_mods.append({
                "filename": e["filename"],
                "sha512": sha,
                "sha1": e["sha1"],
                "size": e["size"],
                "source": "modrinth",
                "modrinth_version_id": info["version_id"],
                "modrinth_project_id": info["project_id"],
                "modrinth_url": info["url"],
            })
            continue

        fp = e.get("fingerprint")
        if fp is not None and int(fp) in curseforge:
            info = curseforge[int(fp)]
            manifest_mods.append({
                "filename": e["filename"],
                "sha512": sha,
                "sha1": e["sha1"],
                "size": e["size"],
                "source": "curseforge",
                "curseforge_mod_id": info["mod_id"],
                "curseforge_file_id": info["file_id"],
            })
            continue

        manifest_mods.append({
            "filename": e["filename"],
            "sha512": sha,
            "sha1": e["sha1"],
            "size": e["size"],
            "source": "unresolved",
        })
        unresolved.append({
            "filename": e["filename"],
            "sha512": sha,
            "sha1": e["sha1"],
            "size": e["size"],
            "reason": "не найден ни в Modrinth, ни в CurseForge",
        })

    version = args.manifest_version or time.strftime("%Y.%m.%d-%H%M")

    manifest = {"manifest_version": version, "mods": manifest_mods}
    if args.archive_url:
        manifest["archive_url"] = args.archive_url

    with args.output.open("w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    print(f"\nМанифест: {args.output} ({len(manifest_mods)} записей)")

    with args.unresolved.open("w", encoding="utf-8") as f:
        json.dump(unresolved, f, ensure_ascii=False, indent=2)
    print(f"Неопознанные: {args.unresolved} ({len(unresolved)} записей)")

    if unresolved:
        print("\nНеопознанные моды (нужно положить в mods.zip на сервере):")
        for u in unresolved:
            print(f"  - {u['filename']}  sha512={u['sha512'][:16]}…")


if __name__ == "__main__":
    main()
