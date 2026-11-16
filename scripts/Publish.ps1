param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputDirectory = Join-Path $projectRoot "artifacts/$Runtime"
if ($Runtime.StartsWith('osx-')) {
    throw 'Build and package macOS on a Mac using scripts/publish.sh.'
}

if ($env:OS -eq 'Windows_NT' -and $Runtime.StartsWith('win-')) {
    $executablePath = Join-Path $outputDirectory 'PartyTime.exe'
    $copies = Get-CimInstance Win32_Process -Filter "Name = 'PartyTime.exe'" |
        Where-Object { [string]::Equals($_.ExecutablePath, $executablePath, [System.StringComparison]::OrdinalIgnoreCase) }
    foreach ($copy in $copies) {
        Stop-Process -Id $copy.ProcessId -ErrorAction Stop
        Wait-Process -Id $copy.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }
}

& dotnet publish (Join-Path $projectRoot 'PartyTime.csproj') -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $outputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Published $Runtime to $outputDirectory"
