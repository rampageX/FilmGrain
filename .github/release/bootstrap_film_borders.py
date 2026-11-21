#!/usr/bin/env python3
"""One-time import of the accepted 15-film-border assets from the pinned v5.0.79 Stable.
Future builds read the committed bundle, never the external release.
"""
import hashlib
import re
import struct
import sys
import zipfile
from pathlib import Path

EXPECTED_STABLE_SHA256 = "3b269a38352cac88a1429cdb3dc453d0f35f7ae98e14d3bac3fcb8819b5e64f5"
PREFIX = "Utils/_FilmBorders/User/"
EXTS = (".png", ".raw", ".label", ".aperture")
NAME = re.compile(r"^[A-F0-9]{32}\.(?:png|raw|label|aperture)$")

def sha(data):
    return hashlib.sha256(data).hexdigest()

def run(source, destination, manifest_path):
    if sha(source.read_bytes()) != EXPECTED_STABLE_SHA256:
        raise ValueError("Stable source SHA256 mismatch; refusing unverified material")
    entries = {}
    with zipfile.ZipFile(source) as stable:
        for name in stable.namelist():
            norm = name.replace("\\", "/")
            if not norm.startswith(PREFIX):
                continue
            suffix = norm[len(PREFIX):]
            if not NAME.fullmatch(suffix):
                raise ValueError("Unexpected User resource in pinned source: " + suffix)
            if suffix in entries:
                raise ValueError("Duplicate border file: " + suffix)
            entries[suffix] = stable.read(name)
    if len(entries) != 60:
        raise ValueError("Expected exactly 60 files for 15 borders: " + str(len(entries)))
    ids = sorted({n.split(".")[0] for n in entries})
    if len(ids) != 15:
        raise ValueError("Expected 15 different border IDs: " + str(len(ids)))
    layouts = {"SIDE": 0, "TOP_BOTTOM": 0}
    for id_ in ids:
        for extension in EXTS:
            if id_ + extension not in entries:
                raise ValueError("Missing file: " + id_ + extension)
        png = entries[id_ + ".png"]
        if len(png) < 33 or png[:8] != b"\x89PNG\r\n\x1a\n":
            raise ValueError("Invalid PNG: " + id_)
        if struct.unpack(">IIBB", png[16:26]) != (1920, 1080, 8, 6):
            raise ValueError("Expected 1920x1080 RGBA8: " + id_)
        if len(entries[id_ + ".raw"]) != 1920 * 1080 * 4:
            raise ValueError("Incorrect RAW length: " + id_)
        label = entries[id_ + ".label"].decode("utf-8-sig").strip()
        if not label or len(label) > 40:
            raise ValueError("Invalid border label: " + id_)
        fields = entries[id_ + ".aperture"].decode("utf-8-sig").strip().split("\t")
        if len(fields) != 3 or fields[2] not in layouts:
            raise ValueError("Invalid aperture layout: " + id_)
        width, height = int(fields[0]), int(fields[1])
        if fields[2] == "SIDE" and not (1600 <= width <= 1780 and 900 <= height <= 1020):
            raise ValueError("Invalid SIDE aperture: " + id_)
        if fields[2] == "TOP_BOTTOM" and not (1600 <= width <= 1910 and 700 <= height <= 860):
            raise ValueError("Invalid TOP_BOTTOM aperture: " + id_)
        layouts[fields[2]] += 1
    if layouts != {"SIDE": 9, "TOP_BOTTOM": 6}:
        raise ValueError("Wrong layout split: " + repr(layouts))
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as output:
        for name in sorted(entries):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o100644 << 16)
            output.writestr(info, entries[name], compress_type=zipfile.ZIP_DEFLATED, compresslevel=6)
    manifest_path.write_text("# FGS preinstalled 15 borders: SHA256  relative filename inside FGS_Bundled_15.zip\n" +
        "\n".join(sha(entries[name]) + "  " + name for name in sorted(entries)) + "\n", encoding="utf-8")
    with zipfile.ZipFile(destination) as check:
        if sorted(check.namelist()) != sorted(entries):
            raise ValueError("Bundle archive entry count mismatch")
        for name in entries:
            if sha(check.read(name)) != sha(entries[name]):
                raise ValueError("Bundle content mismatch: " + name)
    print("PASS: FGS 15 preinstalled borders, 9 SIDE + 6 TOP_BOTTOM, 60 byte-verified files")
    print("Bundle size:", destination.stat().st_size, "bytes")
    print("Bundle SHA256:", sha(destination.read_bytes()))

if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("usage: bootstrap_film_borders.py Stable.zip Bundle.zip manifest.txt")
    run(*(Path(p) for p in sys.argv[1:4]))
