[CmdletBinding()]
param([switch]$Apply,[switch]$SelfTest)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
function Assert-Root([string]$root) {
    if(!(Test-Path -LiteralPath (Join-Path $root 'dotnet\FilmGrain_Studio_NET_Preview.cs') -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $root 'FilmGrain_Config.default.ini') -PathType Leaf)){throw 'Not a FilmGrain project root'}
    if(([IO.File]::GetAttributes($root) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Project root is a link; use its physical location'}
}
# Explicit roots only: never follow a link while discovering or removing children.
function Remove-OwnedNode([IO.FileSystemInfo]$item) {
    $item.Refresh()
    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Write-Host ('[LINK ONLY] '+$item.FullName)
        if(($item.Attributes -band [IO.FileAttributes]::Directory) -ne 0){[IO.Directory]::Delete($item.FullName,$false)}else{[IO.File]::Delete($item.FullName)}
        return
    }
    if(($item.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
        foreach($child in ([IO.DirectoryInfo]$item).GetFileSystemInfos()){Remove-OwnedNode $child}
        [IO.Directory]::Delete($item.FullName,$false)
    } else {
        if(($item.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0){$item.Attributes=$item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)}
        [IO.File]::Delete($item.FullName)
    }
}
function Get-Candidates([string]$root) {
    foreach($entry in ([IO.DirectoryInfo]$root).GetFileSystemInfos()) {
        if($entry.Name -in @('Tests_Presets_R8M3VN','Tests_Cleanup_Presets_R8M3VN','Tests_HDRFRUC_H7M4QX')){$entry;continue}
        if(($entry.Attributes -band [IO.FileAttributes]::Directory) -eq 0 -and (($entry.Name -like 'Return_Presets_R8M3VN_*.zip') -or ($entry.Name -like 'Return_HDRFRUC_H7M4QX_*.zip'))){$entry}
    }

}
function Invoke-OwnedCleanup([string]$root,[bool]$delete) {
    Assert-Root $root
    $items=@(Get-Candidates $root)
    Write-Host ('Root: '+$root)
    foreach($item in $items){Write-Host (($(if($delete){'[DELETE] '}else{'[PLAN] '})+$item.FullName))}
    if(!$delete){Write-Host ('Plan only; candidates='+$items.Count);return}
    foreach($item in $items){Remove-OwnedNode $item}
    if(@(Get-Candidates $root).Count -ne 0){throw 'Cleanup incomplete; candidates remain'}
    Write-Host ('Cleanup complete; candidates='+$items.Count)
}
function Test-Cleanup {
    Assert-Root $project
    $testBase=Join-Path $project 'Tests_Cleanup_Presets_R8M3VN'
    if([IO.Directory]::Exists($testBase) -and ([IO.File]::GetAttributes($testBase) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Cleanup self-test directory is a link'}
    $group=Join-Path $testBase ([Guid]::NewGuid().ToString('N'))
    $fixture=Join-Path $group 'project'
    $outside=Join-Path $group 'private_dependency'
    [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'dotnet'))
    [void][IO.Directory]::CreateDirectory($outside)
    [IO.File]::WriteAllText((Join-Path $outside 'KEEP.txt'),'private sentinel')
    [IO.File]::WriteAllText((Join-Path $fixture 'dotnet\FilmGrain_Studio_NET_Preview.cs'),'fixture')
    [IO.File]::WriteAllText((Join-Path $fixture 'FilmGrain_Config.default.ini'),'fixture')
    $protected=@('UserPresets\quick.json','UserPresets\KEEP.fgspreset.json','Presets\Initial\01.fgspreset.json','_AV1_Grain_Tables\KEEP.tbl','_LUT_Tools\KEEP.cube','FilmGrain_Config.ini','images\KEEP.jpg','Utils\KEEP.txt','_OpenSVPFlow\.venv\KEEP.txt','Logs\KEEP.log','Tests_OtherProject\KEEP.txt','Return_OtherProject.zip','.github\release\ACCEPTANCE_v5.0.20.md')
    foreach($relative in $protected){$path=Join-Path $fixture $relative;[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path));[IO.File]::WriteAllText($path,'protected')}
    $owned=Join-Path $fixture 'Tests_Presets_R8M3VN'
    [void][IO.Directory]::CreateDirectory((Join-Path $owned '样本 空格'' [甲],;&%'))
    [IO.File]::WriteAllText((Join-Path $owned '样本 空格'' [甲],;&%\test.txt'),'owned')
    [IO.File]::WriteAllText((Join-Path $fixture 'Return_Presets_R8M3VN_fixture.zip'),'owned')
    [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'Tests_HDRFRUC_H7M4QX'))
    [IO.File]::WriteAllText((Join-Path $fixture 'Tests_HDRFRUC_H7M4QX\generated.yuv'),'owned HDR sample')
    [IO.File]::WriteAllText((Join-Path $fixture 'Return_HDRFRUC_H7M4QX_fixture.zip'),'owned')
    if(-not ('FgsPresetCleanupJunction' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class FgsPresetCleanupJunction {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int length,IntPtr output,int outputSize,out int returned,IntPtr overlapped);
 public static void Create(string link,string target) {
  Directory.CreateDirectory(link);
  string full=Path.GetFullPath(target);
  string substitute=full.StartsWith(@"\\")?@"\??\UNC\"+full.Substring(2):@"\??\"+full;
  byte[] sub=Encoding.Unicode.GetBytes(substitute),print=Encoding.Unicode.GetBytes(full);
  byte[] data=new byte[16+sub.Length+2+print.Length+2];
  using(var stream=new MemoryStream(data))using(var writer=new BinaryWriter(stream)) {
   writer.Write(0xA0000003u);writer.Write((ushort)(data.Length-8));writer.Write((ushort)0);
   writer.Write((ushort)0);writer.Write((ushort)sub.Length);writer.Write((ushort)(sub.Length+2));writer.Write((ushort)print.Length);
   writer.Write(sub);writer.Write((ushort)0);writer.Write(print);writer.Write((ushort)0);
  }
  using(var handle=CreateFile(link,0x40000000,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero)) {
   if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   int returned;if(!DeviceIoControl(handle,0x900A4,data,data.Length,IntPtr.Zero,0,out returned,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  }
 }
}
'@
    }
    [FgsPresetCleanupJunction]::Create((Join-Path $owned 'private_link'),$outside)
    # Plan must not remove anything. Apply must remove link rather than its target.
    Invoke-OwnedCleanup $fixture $false
    if(![IO.Directory]::Exists($owned)){throw 'Plan modified fixture'}
    Invoke-OwnedCleanup $fixture $true
    foreach($relative in $protected){if([IO.File]::ReadAllText((Join-Path $fixture $relative)) -ne 'protected'){throw ('Protected fixture changed: '+$relative)}}
    if([IO.File]::ReadAllText((Join-Path $outside 'KEEP.txt')) -ne 'private sentinel'){throw 'Junction target changed'}
    Invoke-OwnedCleanup $fixture $true
    Remove-OwnedNode ([IO.DirectoryInfo]$group)
    if(@(([IO.DirectoryInfo]$testBase).GetFileSystemInfos()).Count -eq 0){[IO.Directory]::Delete($testBase,$false)}
    Write-Host 'PASS - plan, deletion, repeat cleanup, protected files and junction target preservation'
}
try {
    if($SelfTest){Test-Cleanup}else{Invoke-OwnedCleanup $project ([bool]$Apply)}
    exit 0
} catch {Write-Host ('FAIL: '+$_.Exception.ToString());exit 1}
