param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime = "win-$Architecture"
$outputDirectory = Join-Path $projectRoot "artifacts/$runtime"
$executablePath = Join-Path $outputDirectory 'PartyTime.exe'

# A tray instance keeps its executable locked even when its controls are closed.
# Only stop instances running from this publish destination.
$runningCopies = Get-CimInstance Win32_Process -Filter "Name = 'PartyTime.exe'" |
    Where-Object { [string]::Equals($_.ExecutablePath, $executablePath, [System.StringComparison]::OrdinalIgnoreCase) }
foreach ($runningCopy in $runningCopies) {
    Write-Host "Stopping PartyTime from $outputDirectory before publishing."
    Stop-Process -Id $runningCopy.ProcessId -ErrorAction Stop
    Wait-Process -Id $runningCopy.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
}

& dotnet publish (Join-Path $projectRoot 'PartyTime.csproj') -c Release -r $runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $outputDirectory
exit $LASTEXITCODE
