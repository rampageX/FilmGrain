OpenSVPFlow runtime for Film Grain Studio v4.8.20 Stable

Use the application's dependency setup / advanced plugin update entry points.
The legacy BAT/PowerShell installers are archived under Legacy and are not called by the .NET runtime.

Bundled plugins: nightly-20261011-deadbadae0d9.
New SmoothFps options: dedup (default 0), mask.still (off), mask.peel (off).
Still defaults: limit 4.5, edge 5, tolerance 30; Peel limit 6.
All extras are off by default. Fixed DLL hash restrictions have been removed.

Advanced settings retain Algo, Analyse Profile, Artifact Mask Area, Speed, Tuning and Block Size.
Target FPS is configurable, including exact fractions and multipliers; default 60.
The verified FGS path uses progressive YUV420P8 + ordinary SmoothFps/OpenCL.
HDR Preserve and interlaced inputs retain their existing SVP bypass rules.
FRUC does not receive these options. Dedup is not a VFR or audio-timestamp repair.

New options are located on the right side of Advanced / Interpolation.
Other DLL builds are allowed. Missing files, plugin load failures and unsupported options are reported at runtime.
