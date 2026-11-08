#!/usr/bin/env python3
"""Export the exact built-in FGS Film Preset models, with no downloads.

Reads the existing FGS C# base64/GZip blobs; intended for an independent lab.
Run from any working directory: python export_presets.py
"""

from __future__ import annotations

import base64
import gzip
from pathlib import Path
import re


HERE = Path(__file__).resolve().parent
SOURCE = HERE.parents[1] / "dotnet" / "FilmGrain_Av1GrainTableSelectionCore.cs"
DEST = HERE / "presets"
PRESETS = (
    "16mm", "16mm-1", "16mm-2", "16mm-3",
    "classic35", "classic35-1", "classic35-2", "classic35-3",
    "modern35", "modern35-1", "modern35-2", "modern35-3",
    "super8", "maxmid",
)


def main() -> None:
    content = SOURCE.read_text(encoding="utf-8-sig")
    matches = dict(re.findall(
        r'case "([^"]+)": encoded=\s*(.*?);\s*break;', content, re.DOTALL
    ))
    if set(PRESETS) != set(matches):
        raise RuntimeError("FGS built-in preset names have changed; review first")
    DEST.mkdir(exist_ok=True)
    for name in PRESETS:
        fragments = re.findall(r'"([A-Za-z0-9+/=]+)"', matches[name])
        if not fragments:
            raise RuntimeError(f"Missing encoded preset: {name}")
        raw = gzip.decompress(base64.b64decode("".join(fragments), validate=True))
        if not raw.startswith(b"filmgrn1\n"):
            raise RuntimeError(f"Unexpected grain model format: {name}")
        target = DEST / f"{name}.tbl"
        target.write_bytes(raw)
        print(f"EXPORTED {name}: {raw.count(b'E ')} events, {len(raw)} bytes")


if __name__ == "__main__":
    main()
