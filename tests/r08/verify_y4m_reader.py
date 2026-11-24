"""Exercise the production reader against real FFmpeg Y4M pipes."""
import ast
import io
from pathlib import Path
import subprocess

path = Path("_OpenSVPFlow/FilmGrain_OpenSVPFlow.vpy")
tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
reader_class = next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == "SdrY4mReader")
import sys
scope = {"sys": sys}
exec(compile(ast.Module(body=[reader_class], type_ignores=[]), str(path), "exec"), scope)
reader = scope["SdrY4mReader"]

for n, d in ((25, 1), (30000, 1001), (60000, 1001)):
    command = ["ffmpeg", "-v", "error", "-f", "lavfi", "-i", f"testsrc2=s=80x48:r={n}/{d}",
               "-frames:v", "36", "-threads:v", "1", "-pix_fmt", "yuv420p10le"]
    expected = subprocess.check_output(command + ["-f", "rawvideo", "-"])
    with subprocess.Popen(command + ["-strict", "-1", "-f", "yuv4mpegpipe", "-"], stdout=subprocess.PIPE) as process:
        source = reader(process.stdout, 80, 48, 60, 1, 36)
        assert (source.fps_num, source.fps_den) == (n, d)
        for i in range(36):
            frame = source.read_frame(i)
            assert frame == expected[i*source.frame_bytes:(i+1)*source.frame_bytes]
            if i > 2:
                assert source.read_frame(i-2) == expected[(i-2)*source.frame_bytes:(i-1)*source.frame_bytes]
        assert process.wait() == 0
        assert len(source.frames) <= 24
        try:
            source.read_frame(0)
        except ValueError as exc:
            assert "outside HDR pipe cache" in str(exc)
        else:
            raise AssertionError("evicted frame was not rejected")

    data = subprocess.check_output(command + ["-strict", "-1", "-f", "yuv4mpegpipe", "-"])
    for payload, count, expected_message in (
        (data[:-1], 36, "Truncated"),
        (data, 35, "more frames"),
        (data, 37, "ended before"),
    ):
        try:
            reader(io.BytesIO(payload), 80, 48, n, d, count).read_frame(count-1)
        except ValueError as exc:
            assert expected_message in str(exc)
        else:
            raise AssertionError("frame count or payload mismatch was not rejected")
    print(f"PASS Y4M {n}/{d}: 36 exact frames, bounded look-behind and EOF validation")
