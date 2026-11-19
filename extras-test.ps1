param(
    [string] $AppPath = (Join-Path $PSScriptRoot 'BetterShot\bin\Release\net10.0-windows10.0.19041.0\BetterShot.exe'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\extras-test'),
    [ValidateSet('extras', 'scrolling', 'background-removal', 'update', 'onboarding')]
    [string] $Area = 'extras'
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $AppPath)) { throw 'BetterShot is not built. Run build.ps1 first.' }
$localDotnetRoot = Join-Path $PSScriptRoot '.tools\dotnet'
if (Test-Path (Join-Path $localDotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnetRoot
    $env:DOTNET_ROOT_X64 = $localDotnetRoot
}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$resultFile = Join-Path $OutputDirectory "$Area-test.txt"
if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile }
$arguments = @("--$Area-test", '--test-output', "`"$OutputDirectory`"")
$process = Start-Process -FilePath $AppPath -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $resultFile) { Get-Content -LiteralPath $resultFile }
    throw "BetterShot $Area test failed with exit code $($process.ExitCode)."
}
Get-Content -LiteralPath $resultFile
