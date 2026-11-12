#!/usr/bin/env bash
set -euo pipefail

# Run inside the MSYS2 UCRT64 shell, not the MSYS or MINGW64 shell.
if [[ "${MSYSTEM:-}" != "UCRT64" ]]; then
    echo 'Open the MSYS2 UCRT64 shell first.' >&2
    exit 2
fi
cd -- "$(dirname -- "$0")"
pkg-config --exists libplacebo
windres --input=preview_icon.rc --output=preview_icon.o --output-format=coff

gcc -std=c11 -O2 -Wall -Wextra -municode -D_WIN32_WINNT=0x0A00 \
    preview_win.c fgs_table.c preview_icon.o -o fgs_gpu_preview.exe \
    $(pkg-config --cflags --libs libplacebo) \
    -ld3d11 -ldxgi -ldxguid -lpsapi -luser32 -lgdi32 -ladvapi32

gcc -std=c11 -O2 -Wall -Wextra -Werror -pedantic \
    test_timeline.c fgs_table.c -o fgs_timeline_test.exe
./fgs_timeline_test.exe presets/*.tbl

gcc -std=c11 -O2 -Wall -Wextra -Werror -pedantic \
    test_plate_overlay.c -o fgs_plate_test.exe -lm
./fgs_plate_test.exe

echo 'Built: fgs_gpu_preview.exe; timeline and Grain Plate math checks passed.'
