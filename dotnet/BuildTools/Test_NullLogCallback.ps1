[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Exe
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw 'FGS test executable missing.' }
$child = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $child -PathType Leaf)) { throw 'Windows PowerShell executable missing.' }

# All reflection arguments are created in compiled C#, not through PowerShell PSObject.
$source = @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

public static class FgsNullLogCallbackRegression
{
    private static int RunOnce(MethodInfo method, string childExe, string workDirectory, Action<string> logger)
    {
        IList<string> args = new List<string> {
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "[Console]::Out.WriteLine('FGS_STDOUT'); [Console]::Error.WriteLine('FGS_STDERR')"
        };
        object[] call = new object[] {
            childExe, args, workDirectory, null, 30000, CancellationToken.None, logger
        };
        return (int)method.Invoke(null, call);
    }

    public static void Check(string fgsExe, string childExe, string workDirectory)
    {
        Assembly assembly = Assembly.LoadFrom(fgsExe);
        Type type = assembly.GetType("FilmGrainStudioPreview.NativeLibraryTools", true);
        MethodInfo method = type.GetMethod("RunProcessEnvironment",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("RunProcessEnvironment missing");

        if (RunOnce(method, childExe, workDirectory, null) != 0)
            throw new InvalidOperationException("Null logger run returned failure.");

        var captured = new ConcurrentQueue<string>();
        Action<string> logger = line => captured.Enqueue(line);
        if (RunOnce(method, childExe, workDirectory, logger) != 0)
            throw new InvalidOperationException("Non-null logger run returned failure.");

        bool stdout = false, stderr = false;
        foreach (string line in captured.ToArray()) {
            if (line.Contains("FGS_STDOUT")) stdout = true;
            if (line.Contains("FGS_STDERR")) stderr = true;
        }
        if (!stdout || !stderr)
            throw new InvalidOperationException("Expected stdout and stderr callbacks were not both observed.");
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp -ErrorAction Stop
[FgsNullLogCallbackRegression]::Check(
    (Resolve-Path -LiteralPath $Exe).Path,
    $child,
    $env:TEMP
)
Write-Host 'PASS: null callback did not crash, and non-null callback received both stdout and stderr.'
