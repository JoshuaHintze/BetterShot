$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'BetterShot\BetterShot.csproj'
$localDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }

function Invoke-DotNet([string[]] $Arguments, [string] $FailureMessage) {
    $process = Start-Process -FilePath $dotnet -ArgumentList $Arguments -WorkingDirectory $PSScriptRoot -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "$FailureMessage (exit code $($process.ExitCode))." }
}

Invoke-DotNet @('restore', "`"$project`"") 'dotnet restore failed'
Invoke-DotNet @('build', "`"$project`"", '-c', 'Release', '--no-restore') 'dotnet build failed'
