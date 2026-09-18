param(
    [string] $AppPath = (Join-Path $PSScriptRoot 'BetterShot\bin\Release\net10.0-windows\BetterShot.exe'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\history-test')
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $AppPath)) { throw 'BetterShot is not built. Run build.ps1 first.' }
$localDotnetRoot = Join-Path $PSScriptRoot '.tools\dotnet'
if (Test-Path (Join-Path $localDotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnetRoot
    $env:DOTNET_ROOT_X64 = $localDotnetRoot
}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$arguments = @('--history-test', '--test-output', "`"$OutputDirectory`"")
$process = Start-Process -FilePath $AppPath -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "BetterShot history test failed with exit code $($process.ExitCode)." }
Get-Content (Join-Path $OutputDirectory 'history-test.txt')