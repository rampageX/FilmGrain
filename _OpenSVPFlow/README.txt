OpenSVPFlow runtime for Film Grain Studio v5.0.63 Stable

This directory ships FilmGrain_OpenSVPFlow.vpy, Check_OpenSVPFlow.vpy,
and the matching Windows svpflow1_vs.dll / svpflow2_vs.dll pair under Plugins.
The private VapourSynth environment (.venv) is deliberately not bundled.

Install / upgrade through FGS native dependency setup and Advanced >
Interpolation > Update OpenSVPFlow plugins. The native setup pins
VapourSynth R82 and BestSource 21.0; the updater preserves the existing
.venv directory, upgrades its packages, and tests the plugin pair before
and after replacement. It backs up the previous DLLs and version marker.

SDR renders YUV420P8. AUTO and Preserve support progressive BT.2020 10-bit 4:2:0
PQ (HDR10) and HLG sources with GPU16 (P8 analysis, P16 GPU
render, P10 output) and CPU10 (P8 analysis, P10 CPU render/output).
CPU10 disables experimental fast analysis and Lean Super and uses cubic:0.
GPU16 and SDR keep their original cubic setting.

See UPGRADE_GUIDE.md for first-time setup, in-place upgrade, compatibility
checks, bundled DLL SHA-256 and recovery. Legacy/_OpenSVPFlow installers
are archived for history and are not called by the native FGS application.
