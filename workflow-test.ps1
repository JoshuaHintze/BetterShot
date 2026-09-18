$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'BetterShot\bin\Release\net10.0-windows\BetterShot.exe'
$localDotnetRoot = Join-Path $PSScriptRoot '.tools\dotnet'
if (!(Test-Path $app)) { throw 'BetterShot is not built. Run build.ps1 first.' }
if (Test-Path (Join-Path $localDotnetRoot 'dotnet.exe')) { $env:DOTNET_ROOT = $localDotnetRoot; $env:DOTNET_ROOT_X64 = $localDotnetRoot }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$arguments = @('--workflow-test', '--test-output', "`"$artifacts`"")
$process = Start-Process -FilePath $app -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -NoNewWindow -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "BetterShot workflow test failed with exit code $($process.ExitCode)." }
Get-Content (Join-Path $artifacts 'workflow-test.txt')
