param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version='0.1.0')
$ErrorActionPreference='Stop'
$projectRoot=$PSScriptRoot
$dotnet=Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$compiler=Join-Path $projectRoot '.tools\InnoSetup\ISCC.exe'
if(-not(Test-Path -LiteralPath $compiler)){throw 'Install Inno Setup 6.7.3 in .tools\InnoSetup first; see installer\README.md.'}
$publish=Join-Path $projectRoot ('artifacts\installer-publish-'+$Version+'-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
& $dotnet publish (Join-Path $projectRoot 'BetterShot\BetterShot.csproj') -c Release -r win-x64 --self-contained true --source https://api.nuget.org/v3/index.json -o $publish "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -nodeReuse:false
if($LASTEXITCODE -ne 0){throw 'Self-contained publish failed.'}
$runtime=Get-Content -LiteralPath (Join-Path $publish 'BetterShot.runtimeconfig.json') -Raw | ConvertFrom-Json
$assets=Get-Content -LiteralPath (Join-Path $projectRoot 'BetterShot\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoot=$assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
foreach($framework in $runtime.runtimeOptions.includedFrameworks){
 $package=$framework.name.ToLowerInvariant()+'.runtime.win-x64'
 $packagePath=Join-Path (Join-Path $packageRoot $package) $framework.version
 foreach($notice in @('LICENSE.TXT','LICENSE','THIRD-PARTY-NOTICES.TXT')){
  $source=Join-Path $packagePath $notice
  if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $publish ('ThirdPartyNotices\'+$framework.name+'-'+$notice+'.txt'))}
 }
}
$output=Join-Path $projectRoot 'artifacts\installer'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $compiler "/DAppVersion=$Version" "/DPublishDir=$publish" "/O$output" (Join-Path $projectRoot 'installer\BetterShot.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
$installer=Join-Path $output "BetterShot-$Version-Setup-x64.exe"
if(-not(Test-Path -LiteralPath $installer)){throw 'Expected installer was not produced.'}
$hash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
[IO.File]::WriteAllText($installer+'.sha256', "$hash  $([IO.Path]::GetFileName($installer))`r`n")
[IO.File]::WriteAllText((Join-Path $output 'latest-publish-path.txt'),$publish)
Get-Item -LiteralPath $installer | Select-Object FullName,Length
'Published payload: '+$publish
'SHA256: '+$hash
'Unsigned development installer; no signing certificate is configured.'