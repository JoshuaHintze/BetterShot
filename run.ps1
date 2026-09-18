$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'BetterShot\bin\Release\net10.0-windows\BetterShot.exe'
$localDotnetRoot = Join-Path $PSScriptRoot '.tools\dotnet'
if (!(Test-Path $app)) { throw 'BetterShot is not built. Run build.ps1 first.' }
if (Test-Path (Join-Path $localDotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnetRoot
    $env:DOTNET_ROOT_X64 = $localDotnetRoot
}
$process = Start-Process -FilePath $app -WorkingDirectory $PSScriptRoot -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "BetterShot exited with error code $($process.ExitCode)." }
