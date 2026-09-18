$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'BetterShot\bin\Release\net10.0-windows\BetterShot.exe'
$localDotnetRoot = Join-Path $PSScriptRoot '.tools\dotnet'
if (!(Test-Path $app)) { throw 'BetterShot is not built. Run build.ps1 first.' }
if (Test-Path (Join-Path $localDotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnetRoot
    $env:DOTNET_ROOT_X64 = $localDotnetRoot
}
$output = Join-Path $PSScriptRoot 'artifacts\smoke-test.png'
New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
$arguments = @('--smoke-test', '--test-output', "`"$output`"")
$process = Start-Process -FilePath $app -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -NoNewWindow -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "BetterShot display capture smoke test failed with exit code $($process.ExitCode)." }
Write-Host "Saved $output"
