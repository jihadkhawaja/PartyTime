param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)
& (Join-Path $PSScriptRoot 'Publish.ps1') -Runtime "win-$Architecture"
exit $LASTEXITCODE
