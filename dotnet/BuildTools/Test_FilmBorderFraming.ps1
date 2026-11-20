# Verifies the production FilmBorderFramingCore implementation without copying its formulas.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$path = Join-Path $root 'dotnet\FilmGrain_NativeHevcCore.cs'
$source = [IO.File]::ReadAllText($path)
$start = $source.IndexOf('internal static class FilmBorderFramingCore', [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Framing core missing' }
$open = $source.IndexOf('{', $start)
if ($open -lt 0) { throw 'Framing body missing' }
$depth = 0
$close = -1
for ($i=$open; $i -lt $source.Length; $i++) {
    if ($source[$i] -eq '{') { $depth++ }
    elseif ($source[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $close=$i; break }
    }
}
if ($close -lt 0) { throw 'Framing core braces unbalanced' }
$class = $source.Substring($start, $close-$start+1)
$test = @'
namespace FilmGrainStudioPreview
{
    public static class FilmBorderFramingAudit
    {
        private static void Check(bool ok,string name) {if(!ok)throw new Exception(name);}
        public static string Filter(int w,int h,double x,double y,double z)
        {return FilmBorderFramingCore.CpuScaleCrop(w,h,x,y,z);}
        public static void Run()
        {
            Check(FilmBorderFramingCore.Neutral(0.5,0.5,1),"neutral values");
            Check(!FilmBorderFramingCore.Neutral(0.5,0.5,1.5),"non-neutral zoom");
            Check(FilmBorderFramingCore.Parse("NaN",0.5,0,1)==0.5,"nonfinite fallback");
            Check(FilmBorderFramingCore.Parse("-3",0.5,0,1)==0,"focus clamp");
            Check(FilmBorderFramingCore.Parse("4",1,1,3)==3,"zoom clamp");
            string expected="scale=1680:748:force_original_aspect_ratio=increase:flags=lanczos,crop=1680:748";
            Check(Filter(1680,748,0.5,0.5,1)==expected,"byte-identical neutral graph");
            double[] zooms={1,1.5,2,3};
            double[] focus={0,0.5,1};
            foreach(double z in zooms)
            foreach(double x in focus)
            foreach(double y in focus) {
                string filter=Filter(1630,698,x,y,z);
                Check(filter.Contains("force_original_aspect_ratio=increase"),"cover");
                if(!FilmBorderFramingCore.Neutral(x,y,z)) {
                    Check(filter.Contains("max(0,min(iw-ow,"),"horizontal clamp");
                    Check(filter.Contains("max(0,min(ih-oh,"),"vertical clamp");
                }
            }
        }
    }
}
'@
$wrapper = "using System; using System.Globalization; namespace FilmGrainStudioPreview { " + $class + " } " + $test
Add-Type -TypeDefinition $wrapper -Language CSharp -ErrorAction Stop
[FilmGrainStudioPreview.FilmBorderFramingAudit]::Run()
Write-Host 'PASS Film Border FFmpeg framing core: neutral, 1.5x/2x/3x, anchors and clamps'

if ([string]::IsNullOrWhiteSpace($env:FGS_MSYS2_ROOT)) { throw 'FGS_MSYS2_ROOT not supplied by Windows runner' }
$bin = Join-Path $env:FGS_MSYS2_ROOT 'ucrt64\bin'
$ffmpegExe = Join-Path $bin 'ffmpeg.exe'
if (-not (Test-Path -LiteralPath $ffmpegExe)) { throw "UCRT64 FFmpeg is missing: $ffmpegExe" }
$env:PATH = $bin + [IO.Path]::PathSeparator + $env:PATH
& $ffmpegExe -hide_banner -version | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg dependency probe failed' }
# Use named fields; PowerShell array nesting and PSObject wrapping are not relied on.
    $cases = @(
        [pscustomobject]@{W=1680;H=748;X=0.5;Y=0.5;Z=1.0}
        [pscustomobject]@{W=1680;H=748;X=0.5;Y=0.5;Z=1.5}
        [pscustomobject]@{W=1680;H=748;X=0.0;Y=1.0;Z=2.0}
        [pscustomobject]@{W=1630;H=698;X=1.0;Y=0.0;Z=3.0}
        [pscustomobject]@{W=1730;H=798;X=0.25;Y=0.75;Z=1.5}
    )
    foreach ($row in $cases) {
        $filter = [FilmGrainStudioPreview.FilmBorderFramingAudit]::Filter(
            [int]$row.W,[int]$row.H,[double]$row.X,[double]$row.Y,[double]$row.Z)
        & $ffmpegExe -hide_banner -loglevel error -f lavfi -i 'testsrc2=s=1920x1080:r=1' -vf $filter -frames:v 1 -f null NUL
        if ($LASTEXITCODE -ne 0) { throw "FFmpeg framing filter failed: $filter" }
    }
    Write-Host 'PASS FFmpeg live filter graph: neutral, zoom, corners, custom apertures'

