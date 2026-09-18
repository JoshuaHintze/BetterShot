$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'BetterShot\bin\Release\net10.0-windows\BetterShot.exe'
$localDotnetRoot = Join-Path $PSScriptRoot '.tools\dotnet'
if (!(Test-Path $app)) { throw 'BetterShot is not built. Run build.ps1 first.' }
if (Test-Path (Join-Path $localDotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnetRoot
    $env:DOTNET_ROOT_X64 = $localDotnetRoot
}
$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null

$overlayResult = Join-Path $artifacts 'overlay-test.txt'
$overlayArguments = @('--overlay-test', '--test-output', "`"$overlayResult`"")
$process = Start-Process -FilePath $app -ArgumentList $overlayArguments -WorkingDirectory $PSScriptRoot -NoNewWindow -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "BetterShot overlay test failed with exit code $($process.ExitCode)." }
Get-Content $overlayResult

$uiResult = Join-Path $artifacts 'main-window.png'
$uiArguments = @('--ui-test', '--test-output', "`"$uiResult`"")
$process = Start-Process -FilePath $app -ArgumentList $uiArguments -WorkingDirectory $PSScriptRoot -NoNewWindow -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "BetterShot UI render test failed with exit code $($process.ExitCode)." }
Write-Host "Saved $uiResult"

& (Join-Path $PSScriptRoot 'workflow-test.ps1')
