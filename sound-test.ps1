param(
    [string] $AppPath = (Join-Path $PSScriptRoot 'BetterShot\bin\Release\net10.0-windows10.0.19041.0\BetterShot.exe'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\sound-test')
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$process = Start-Process -FilePath $AppPath -ArgumentList @('--sound-test', '--test-output', ('"' + $OutputDirectory + '"')) -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "BetterShot sound test failed with exit code $($process.ExitCode)." }
Get-Content (Join-Path $OutputDirectory 'sound-test.txt')
