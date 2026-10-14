param([switch]$Restore, [switch]$Test, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.cache\dotnet'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot '.cache\nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $projectRoot '.cache\nuget-plugins'
$dotnet = (Get-Command dotnet).Source
$sdkLine = & $dotnet --list-sdks | Where-Object { $_ -match '^10\.0\.' } | Select-Object -Last 1
if (-not $sdkLine -or $sdkLine -notmatch '^(\S+) \[(.+)\]') { throw '.NET 10 SDK is required.' }
$msbuild = Join-Path $Matches[2] "$($Matches[1])\MSBuild.dll"
if ($Restore) {
    & $dotnet $msbuild "$projectRoot\FocusLedger.slnx" -t:Restore -verbosity:minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
}
& $dotnet $msbuild "$projectRoot\FocusLedger.slnx" -t:Build -p:Configuration=Release -p:UseSharedCompilation=false -verbosity:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed. On first use, run build.ps1 -Restore after approving the dependency download.' }
if ($Test) {
    & "$projectRoot\tests\FocusLedger.Tests\bin\Release\net10.0\win-x64\FocusLedger.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if ($Publish) {
    & $dotnet $msbuild "$projectRoot\src\FocusLedger.App\FocusLedger.App.csproj" -t:Publish -p:Configuration=Release -p:UseSharedCompilation=false "-p:PublishDir=$projectRoot\publish\" -verbosity:minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
