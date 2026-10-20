#!/usr/bin/env python3
"""Download a NuGet package closure into a local flat feed (offline build support).

The DSH sandbox blocks schannel TLS from dotnet/curl, but python's OpenSSL works,
so we resolve and download the nupkg closure ourselves and point NuGet at the folder.
"""
import json
import os
import re
import sys
import time
import urllib.request
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

FEED = Path(__file__).resolve().parents[1] / "_nuget" / "feed"
TARGET = (8, 0, "windows", (10, 0, 19041))

_index_cache = {}


def http_json(url):
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=60) as r:
                return json.loads(r.read().decode("utf-8"))
        except Exception as e:
            if attempt == 2:
                raise
            time.sleep(1 + attempt)


def http_bytes(url):
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=180) as r:
                return r.read()
        except Exception as e:
            if attempt == 2:
                raise
            time.sleep(1 + attempt)


def versions(pkg):
    pkg = pkg.lower()
    if pkg not in _index_cache:
        _index_cache[pkg] = http_json(
            f"https://api.nuget.org/v3-flatcontainer/{pkg}/index.json"
        )["versions"]
    return _index_cache[pkg]


def vtuple(v):
    core = v.split("-")[0].split("+")[0]
    parts = [int(x) if x.isdigit() else 0 for x in core.split(".")]
    while len(parts) < 4:
        parts.append(0)
    stable = "-" not in v
    return tuple(parts[:4]), stable


def parse_tfm(tfm):
    t = (tfm or "").strip().lower()
    if not t:
        return None
    t = t.replace(".netstandard", "netstandard").replace(".netcoreapp", "netcoreapp")
    t = t.replace(".netframework", "net").replace("netframework", "net")
    t = t.strip(".")
    if t.startswith("netstandard"):
        ver = t[len("netstandard"):]
        return ("netstandard", tuple(int(x) for x in ver.split(".") if x.isdigit()), None, None)
    if t.startswith("netcoreapp"):
        ver = t[len("netcoreapp"):]
        return ("netcoreapp", tuple(int(x) for x in ver.split(".") if x.isdigit()), None, None)
    if t.startswith("uap"):
        return ("uap", (), None, None)
    m = re.match(r"^net(\d+)(?:\.(\d+))?(?:-(windows|android|ios|macos)([\d.]+)?)?$", t)
    if not m:
        return ("unknown", (), None, None)
    major = int(m.group(1))
    minor = int(m.group(2)) if m.group(2) else 0
    plat = m.group(3)
    plat_ver = None
    if m.group(4):
        plat_ver = tuple(int(x) for x in m.group(4).rstrip(".").split(".") if x.isdigit())
    # "net472" style: major=4 minor=72 -> .NET Framework
    if major == 4 and m.group(2) and len(m.group(2)) == 2:
        return ("netframework", (4, int(m.group(2)[0]), int(m.group(2)[1])), None, None)
    return ("net", (major, minor), plat, plat_ver)


def compat(pkg_tfm, target_tfm):
    p = parse_tfm(pkg_tfm)
    if p is None:
        return False
    fam, ver, plat, pver = p
    tfam, tver, tplat, tpver = target_tfm
    if fam == "netstandard":
        return True
    if fam == "unknown":
        return False
    if fam == "netframework":
        return False
    if fam == "uap":
        return False
    if fam != tfam:
        return False
    if ver > tver:
        return False
    if plat:
        if plat != tplat:
            return False
        if pver and tpver and pver > tpver:
            return False
    return True


def score(pkg_tfm):
    parsed = parse_tfm(pkg_tfm)
    if parsed is None:
        return (-1, (), 0, ())
    fam, ver, plat, pver = parsed
    famrank = {"net": 100, "netcoreapp": 90, "netstandard": 10}.get(fam, 0)
    return (famrank, ver, 1 if plat else 0, pver or ())


def pick_group(groups, target_tfm):
    cands = [g for g in groups if compat(g[0], target_tfm)]
    if not cands:
        cands = [g for g in groups if g[0] is None]
    if not cands:
        return [], None
    best = max(cands, key=lambda g: score(g[0]))
    return best[1], best[0]


def parse_range(rng):
    rng = (rng or "").strip()
    if not rng:
        return None, None
    if rng.startswith("["):
        inner = rng.strip("[]()")
        parts = [p.strip() for p in inner.split(",")]
        if len(parts) == 1:
            return parts[0], parts[0]
        lo = parts[0] or None
        hi = parts[1] or None
        return lo, hi
    return rng, None


def resolve_version(pkg, rng):
    lo, hi = parse_range(rng)
    allv = versions(pkg)
    stable = [v for v in allv if "-" not in v]
    if lo:
        lo_t, _ = vtuple(lo)
        cand = [v for v in stable if vtuple(v)[0] >= lo_t]
        if hi:
            hi_t, _ = vtuple(hi)
            if hi.strip("[]") == hi:
                cand = [v for v in cand if vtuple(v)[0] <= hi_t]
            else:
                cand = [v for v in cand if vtuple(v)[0] < hi_t]
        if cand:
            return cand[0]
        if lo in allv:
            return lo
    # fall back to exact lo string, else latest stable
    if lo and lo in allv:
        return lo
    return stable[-1] if stable else allv[-1]


def nuspec_deps(nupkg_path):
    with zipfile.ZipFile(nupkg_path) as z:
        name = next(n for n in z.namelist() if n.lower().endswith(".nuspec"))
        data = z.read(name)
    root = ET.fromstring(data)
    ns = {"n": root.tag.split("}")[0].strip("{")} if "}" in root.tag else {}
    meta = root.find("n:metadata", ns) if ns else root.find("metadata")
    if meta is None:
        meta = root
    deps_node = meta.find("n:dependencies", ns) if ns else meta.find("dependencies")
    groups = []
    if deps_node is not None:
        found_groups = deps_node.findall("n:group", ns) if ns else deps_node.findall("group")
        if found_groups:
            for g in found_groups:
                tfm = g.get("targetFramework")
                dl = []
                for d in (g.findall("n:dependency", ns) if ns else g.findall("dependency")):
                    dl.append((d.get("id"), d.get("version")))
                groups.append((tfm, dl))
        else:
            dl = []
            for d in (deps_node.findall("n:dependency", ns) if ns else deps_node.findall("dependency")):
                dl.append((d.get("id"), d.get("version")))
            groups.append((None, dl))
    return groups


def main():
    targets = []
    for arg in sys.argv[1:]:
        if ":" in arg:
            i, v = arg.split(":", 1)
            targets.append((i, v))
    FEED.mkdir(parents=True, exist_ok=True)
    seen = set()
    queue = list(targets)
    while queue:
        pkg, ver = queue.pop(0)
        pkg = pkg.strip()
        key = (pkg.lower(), ver.lower())
        if key in seen:
            continue
        seen.add(key)
        dest = FEED / f"{pkg.lower()}.{ver.lower()}.nupkg"
        if not dest.exists():
            url = f"https://api.nuget.org/v3-flatcontainer/{pkg.lower()}/{ver.lower()}/{pkg.lower()}.{ver.lower()}.nupkg"
            print(f"GET {pkg} {ver}", flush=True)
            data = http_bytes(url)
            dest.write_bytes(data)
            print(f"  -> {dest.name} ({len(data)/1048576:.1f} MB)", flush=True)
        else:
            print(f"HAVE {pkg} {ver}", flush=True)
        try:
            groups = nuspec_deps(dest)
        except Exception as e:
            print(f"  ! cannot read nuspec: {e}", flush=True)
            continue
        deps, tfm = pick_group(groups, TARGET)
        for did, rng in deps:
            if not did:
                continue
            dv = resolve_version(did, rng)
            print(f"  dep {did} {rng} -> {dv} (tfm {tfm})", flush=True)
            queue.append((did, dv))
    print("DONE", len(seen), "packages in", FEED, flush=True)


if __name__ == "__main__":
    main()
