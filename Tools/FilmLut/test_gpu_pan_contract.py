"""Static regression checks for FGS color/LUT preview pan architecture.

Run: python Tools/FilmLut/test_gpu_pan_contract.py
No Windows GUI or GPU is required; Windows interaction remains a manual test.
"""
from pathlib import Path


root = Path(__file__).resolve().parents[2]
source = (root / "dotnet/FilmGrain_NativeColorCorrectionForm.cs").read_text(encoding="utf-8-sig")
builder = (root / "dotnet/build_NET_Preview.bat").read_text(encoding="utf-8")


def method(name):
    start = source.index("private void " + name + "(")
    end = source.find("\n        private ", start + 1)
    return source[start:] if end < 0 else source[start:end]


def check(condition, message):
    if not condition:
        raise AssertionError(message)
    print("PASS: " + message)


drag = method("PictureMouseMove")
release = method("PictureMouseUp")
renderer = source[source.index("private Bitmap RenderPreview(") : source.index("private void CancelActiveRender(")]
update = method("UpdateGpuDisplay")
holder = method("UpdateOriginalHold")

check("RequestRender(" not in drag and "RenderPreview(" not in drag, "no FFmpeg/render on mouse movement")
check("UpdateGpuDisplay()" in drag and "pic.Invalidate()" in drag, "GPU transform + cached GDI fallback on pan")
check("RequestRender(" not in release, "no FFmpeg/render on mouse release")
check('if(state.Zoom)prefix.Add("null")' in renderer, "zoom renders uncropped native source once")
check("if(!state.Zoom)filter += ", "pad only applies to fitted preview")
check("zoomOriginalImage" in holder and "zoomFullImage" in holder, "original bypass selects cached native frame")
check("WpfCanvas.SetLeft" in update and "WpfCanvas.SetTop" in update, "GPU pan modifies texture coordinates")
check("UploadGpuFrame(" not in drag and "UploadGpuFrame(" not in update, "no GPU texture upload on pan")
check("DeleteObject(handle)" in source, "GDI bitmap handles released after texture upload")
check("zoomFullImage.Dispose()" in source and "zoomOriginalImage.Dispose()" in source, "native frame buffers released")
check(builder.count("/reference:PresentationCore.dll") == 2, "both Windows build paths reference WPF")
check('"eq=contrast="' in renderer and "lut3d=file='" in renderer, "existing FFmpeg grading and LUT filters preserved")
