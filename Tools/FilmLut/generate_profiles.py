"""Generate FGS's small, curated film-print LUT set from Spectral Film LUT.

Development-time tool only. No Python runtime or upstream GUI is required by FGS.

Required upstream: https://github.com/JanLohse/spectral_film_lut at v0.18.13
The generated files remain subject to upstream's MIT attribution.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys


UPSTREAM_COMMIT = "bcbcfc6861f49cc706dc5d00ea048e4853060aab"
PROFILES = (
    ("Vision3_50D_2383", "Kodak Vision3 50D 5203", "Kodak Vision 2383"),
    ("Vision3_250D_2383", "Kodak Vision3 250D 5207", "Kodak Vision 2383"),
    ("Vision3_500T_2383", "Kodak Vision3 500T 5219", "Kodak Vision 2383"),
    ("Vision3_500T_2393", "Kodak Vision3 500T 5219", "Kodak Vision Premier 2393"),
    ("Eterna500_3513DI", "Fuji Eterna 500", "Fuji Eterna-CP Type 3513DI"),
)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", type=Path, required=True)
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).resolve().parents[2] / "_LUT_Tools" / "FilmLut" / "Profiles",
    )
    parser.add_argument("--size", type=int, default=33)
    args = parser.parse_args()
    if not 2 <= args.size <= 65:
        parser.error("LUT size must be 2..65")

    upstream = args.upstream.resolve()
    commit = subprocess.check_output(
        ["git", "-C", str(upstream), "rev-parse", "HEAD"], text=True
    ).strip()
    if commit != UPSTREAM_COMMIT:
        parser.error(f"Wrong upstream revision: {commit}; expected {UPSTREAM_COMMIT}")
    sys.path.insert(0, str(upstream / "src"))
    from spectral_film_lut import FILM_STOCKS
    from spectral_film_lut.film_spectral import FilmSpectral
    from spectral_film_lut.utils import create_lut

    names = {name for _, negative, print_stock in PROFILES for name in (negative, print_stock)}
    names.add("Kodak Vision3 250D 5207")
    by_name = {raw.name: FilmSpectral(raw) for raw in FILM_STOCKS if raw.name in names}
    args.output.mkdir(parents=True, exist_ok=True)
    results = []
    for stem, negative_name, print_name in PROFILES:
        negative = by_name[negative_name]
        print_stock = by_name[print_name]
        target = args.output / (stem + ".cube")
        # All five share an identical input/output contract: SDR BT.709 signals.
        # SFL performs its own input transfer decoding and the chosen print model.
        # No ADX/APD intermediate is needed for a single, combined full LUT.
        generated = create_lut(
            negative,
            print_stock,
            lut_size=args.size,
            name=str(target.with_suffix("")),
            cube=True,
            input_colorspace="ITU-R BT.709",
            output_gamut="Rec. 709",
            gamma_func="Gamma 2.4",
            projector_kelvin=6500,
            exp_comp=0.0,
            white_clip=False,
            white_balance=0,
            exp_kelvin=6500,
            mode="full",
            red_light=0.0,
            green_light=0.0,
            blue_light=0.0,
            shadow_comp=0.0,
            color_masking=negative.color_masking,
            tint=0.0,
            sat_adjust=1.0,
            adx_scaling=4.0,
            push_pull=0.0,
            inversion=False,
            inversion_gamma=3.0,
            idealized_curve=False,
            apd_intermediate=False,
            filter_mode=None,
            custom_inversion=False,
            upsampling_method="SFL upsampling",
            reference_negative=by_name["Kodak Vision3 250D 5207"],
        )
        if Path(generated) != target:
            raise RuntimeError(f"Unexpected LUT path: {generated}")
        lines = target.read_text(encoding="utf-8").splitlines()
        data = []
        for line in lines:
            parts = line.split()
            if len(parts) != 3:
                continue
            try:
                values = tuple(float(part) for part in parts)
            except ValueError:
                continue
            if not all(math.isfinite(v) for v in values):
                raise RuntimeError(f"Non-finite value in {target}")
            data.append(values)
        if len(data) != args.size**3:
            raise RuntimeError(f"Incomplete {target}: {len(data)} entries")
        results.append(
            {
                "id": stem,
                "negative": negative_name,
                "print": print_name,
                "file": target.name,
                "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
                "min": round(min(min(row) for row in data), 7),
                "max": round(max(max(row) for row in data), 7),
            }
        )
        print(f"{stem}: {target.stat().st_size} bytes, output {results[-1]['min']}..{results[-1]['max']}")

    manifest = {
        "generator": "JanLohse/spectral_film_lut",
        "commit": UPSTREAM_COMMIT,
        "license": "MIT",
        "lut_size": args.size,
        "input": "ITU-R BT.709 (transfer decoding by colour-science)",
        "output": "Rec.709 / Gamma 2.4",
        "mode": "full, direct spectral printing (APD off)",
        "profiles": results,
    }
    (args.output.parent / "profiles.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    main()
