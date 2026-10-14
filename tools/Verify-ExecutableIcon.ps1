param([string]$Exe = (Join-Path $PSScriptRoot '..\publish\FocusLedger.exe'), [string]$Icon = (Join-Path $PSScriptRoot '..\src\FocusLedger.App\Assets\focusledger.ico'), [string]$Output = "", [switch]$RefreshShell)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class IconResources {
 [DllImport("shell32.dll", CharSet=CharSet.Unicode)] public static extern void SHChangeNotify(uint eventId,uint flags,string item,IntPtr other);
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string name,IntPtr file,uint flags);
 [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
 [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
 [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
 [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module,IntPtr resource);
 delegate bool EnumProc(IntPtr module,IntPtr type,IntPtr name,IntPtr param);
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool EnumResourceNames(IntPtr module,IntPtr type,EnumProc callback,IntPtr param);
 public static Dictionary<string,byte[]> Read(string path) {
  var module=LoadLibraryEx(path,IntPtr.Zero,2); if(module==IntPtr.Zero) throw new Exception("Cannot read executable resources");
  var result=new Dictionary<string,byte[]>();
  try { foreach(int type in new[]{14,3}) { EnumProc callback=(m,t,n,p)=>{var r=FindResource(m,n,t);var b=new byte[SizeofResource(m,r)];Marshal.Copy(LockResource(LoadResource(m,r)),b,0,b.Length);result.Add(type+":"+n.ToInt64(),b);return true;};EnumResourceNames(module,(IntPtr)type,callback,IntPtr.Zero); } }
  finally { FreeLibrary(module); } return result;
 }
}
'@
if ($Output) { New-Item -ItemType Directory -Force -Path $Output | Out-Null }
$resources = [IconResources]::Read($Exe)
$source = [IO.File]::ReadAllBytes($Icon)
$count = [BitConverter]::ToUInt16($source,4)
$expected = @{}
for($i=0;$i -lt $count;$i++) {
 $entry = 6+16*$i
 $size = if($source[$entry] -eq 0){256}else{[int]$source[$entry]}
 $length = [BitConverter]::ToUInt32($source,$entry+8); $offset = [BitConverter]::ToUInt32($source,$entry+12)
 $expected[$size] = [Convert]::ToBase64String($source,$offset,$length)
}
$matched = $true
foreach($key in $resources.Keys | Where-Object { $_.StartsWith('14:') }) {
 $group = $resources[$key]; $entries = [BitConverter]::ToUInt16($group,4)
 if ($entries -ne $count) { throw "Executable icon frame count differs from source" }
 for($i=0;$i -lt $entries;$i++) {
  $entry=6+14*$i; $size=if($group[$entry] -eq 0){256}else{[int]$group[$entry]}
  $id=[BitConverter]::ToUInt16($group,$entry+12); $bytes=$resources['3:'+ $id]
  $same = $expected[$size] -eq [Convert]::ToBase64String($bytes)
  $matched = $matched -and $same
  Write-Verbose "${size}px: matches source ICO = $same"
  if($Output -and $bytes[0] -eq 137 -and $bytes[1] -eq 80) { [IO.File]::WriteAllBytes((Join-Path $Output "exe-$size.png"),$bytes) }
 }
}
if(-not $matched -or -not ($resources.Keys | Where-Object { $_.StartsWith('14:') })) { throw 'Executable icon resources differ from source icon' }

Write-Output "PASS: executable contains all source icon frames."
if ($RefreshShell) { [IconResources]::SHChangeNotify(0x2000,0x1005,[IO.Path]::GetFullPath($Exe),[IntPtr]::Zero); Write-Output "Requested Explorer icon refresh for this executable only." }
