param([switch]$CleanPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = [IO.Path]::GetFullPath((Join-Path $projectRoot 'publish'))
if ($publish -ne ($projectRoot.TrimEnd('\') + '\publish')) { throw 'Unexpected publish target.' }
if ($CleanPublish -and (Test-Path -LiteralPath $publish)) {
    $items = @((Get-Item -LiteralPath $publish -Force)) + @(Get-ChildItem -LiteralPath $publish -Recurse -Force)
    if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Refusing to delete publish containing links or junctions.' }
    $running = Get-Process -Name FocusLedger -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($publish + '\', [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw 'Exit the published FocusLedger from its tray before cleaning publish.' }
    Remove-Item -LiteralPath $publish -Recurse -Force
}
& (Join-Path $projectRoot 'build.ps1') -Test -Publish
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\FocusLedger.App\FocusLedger.App.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected package version.' }
$name = "FocusLedger-v$version-win-x64"
$artifacts = Join-Path $projectRoot 'artifacts'
$stageParent = Join-Path $artifacts ('stage-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $stageParent $name
New-Item -ItemType Directory -Path $stage -Force | Out-Null
# Explicit allowlist: never ship data directories, local settings, PDBs or bootstrap paths.
$files = @('FocusLedger.exe', 'FocusLedger.dll', 'FocusLedger.Core.dll', 'FocusLedger.Storage.dll',
    'FocusLedger.deps.json', 'FocusLedger.runtimeconfig.json', 'Microsoft.Data.Sqlite.dll',
    'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll',
    'e_sqlite3.dll', 'LICENSE', 'NOTICE', 'README.md')
foreach ($file in $files) {
    $source = Join-Path $publish $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing release file: $file" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage $file)
}
New-Item -ItemType Directory -Path (Join-Path $stage 'licenses'),(Join-Path $stage 'docs\images') -Force | Out-Null
foreach ($license in @('Microsoft.Data.Sqlite-MIT.txt', 'SQLitePCLRaw-Apache-2.0.txt')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "licenses\$license") -Destination (Join-Path $stage "licenses\$license")
}
foreach ($preview in @('overview.png', 'dark.png')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "docs\images\$preview") -Destination (Join-Path $stage "docs\images\$preview")
}
$guide = @'
FocusLedger · 专注时账

使用要求：Windows x64 与 .NET 10 Windows Desktop Runtime（x64）。
运行时下载：https://dotnet.microsoft.com/download/dotnet/10.0
请选择 .NET Desktop Runtime，不是 ASP.NET Core Runtime。

将整个压缩包解压到可写文件夹，再双击 FocusLedger.exe；不要只复制 exe 或直接在压缩包内运行。
在“设置 → 计时与软件”中自定义统计软件和空闲阈值。
默认记录保存在 exe 同级 data 文件夹，可在设置中更改位置。
关闭窗口后继续在托盘记录，从托盘选择“退出”可完全关闭。

更新时先退出旧版，保留原有 data 文件夹和 focusledger.location.json，再替换程序文件。
本包不附带测试记录或预设的个人数据路径。程序未作代码签名。
详细说明见 README.md。

项目与源代码：https://github.com/LightRill/FocusLedger
许可证：GPL-3.0-only，详见 LICENSE、NOTICE 与 licenses 文件夹。
'@
[IO.File]::WriteAllText((Join-Path $stage '开始使用.txt'), $guide, (New-Object Text.UTF8Encoding($true)))
$zip = Join-Path $artifacts ($name + '.zip')
if (Test-Path -LiteralPath $zip) { throw "Package already exists: $zip. Use a new version or review the old artifact before replacing it." }
Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $artifacts ($name + '.sha256')), "$hash  $name.zip`n", (New-Object Text.UTF8Encoding($false)))
Write-Output "Release archive: $zip"
Write-Output "SHA256: $hash"
