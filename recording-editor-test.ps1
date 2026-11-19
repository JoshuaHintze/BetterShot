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
# Pure editor checks run everywhere: cut math, keys, marks, tracking, project v2 (mutes; v1 files still read),
# separate System/Mic WAV tracks written by the mixer and aligned sample-for-sample with the mix, waveform envelopes,
# mute-aware re-mixing, lossless-trim planning, export size estimates, and filmstrip tiling. When Windows media editing
# is available it also encodes a synthetic clip (no desktop content), exports a precise trim, a burned-in redaction, a
# GIF, a timelapse, a stream-copied (lossless) trim, and mic-muted re-mixed MP4s from it, checks that a canceled export
# leaves nothing behind, and renders the editor (filmstrip, waveforms, click/key events) to recording-editor.png at
# 1280x800 and recording-editor-min.png at the minimum window size in the output folder. Inspect both images.
$arguments = @('--recording-editor-test', '--test-output', "`"$OutputDirectory`"")
$process = Start-Process -FilePath $AppPath -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $resultFile) { Get-Content -LiteralPath $resultFile }
    throw "BetterShot recording editor test failed with exit code $($process.ExitCode)."
}
Get-Content -LiteralPath $resultFile
