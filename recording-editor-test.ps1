param(
    [string] $AppPath = (Join-Path $PSScriptRoot 'BetterShot\bin\Release\net10.0-windows10.0.19041.0\BetterShot.exe'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\recording-editor-test')
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $AppPath)) { throw 'BetterShot is not built. Run build.ps1 first.' }
$localDotnetRoot = Join-Path $PSScriptRoot '.tools\dotnet'
if (Test-Path (Join-Path $localDotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnetRoot
    $env:DOTNET_ROOT_X64 = $localDotnetRoot
}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$resultFile = Join-Path $OutputDirectory 'recording-editor-test.txt'
if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile -Force }
# Pure editor checks run everywhere. When Windows media editing is available it also encodes a synthetic clip
# (no desktop content), exports a precise trim, a burned-in redaction, a GIF, and a timelapse from it, checks that a
# canceled export leaves nothing behind, and renders the editor window to recording-editor.png in the output folder.
$arguments = @('--recording-editor-test', '--test-output', "`"$OutputDirectory`"")
$process = Start-Process -FilePath $AppPath -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $resultFile) { Get-Content -LiteralPath $resultFile }
    throw "BetterShot recording editor test failed with exit code $($process.ExitCode)."
}
Get-Content -LiteralPath $resultFile
