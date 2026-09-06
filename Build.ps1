param(
    [string]$DotNet = 'dotnet',
    [string]$InnoCompiler = 'ISCC.exe',
    [string]$CacheRoot = ''
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CacheRoot)) { $CacheRoot = Join-Path $root '.build-cache' }
New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $CacheRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $CacheRoot 'nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $CacheRoot 'nuget-http-cache'
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $CacheRoot 'bundle-cache'
$project = Join-Path $root 'src\ThermalDot.csproj'
$driver = Join-Path $root 'components\PawnIO_setup.exe'
if ((Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash -ne '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032') { throw 'Unexpected PawnIO installer' }
& $DotNet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed' }
& $DotNet publish $project --no-restore -c Release -o (Join-Path $root 'publish') --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
& $InnoCompiler (Join-Path $root 'setup.iss') /Qp
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Write-Output ('Installer: ' + (Join-Path $root 'release\温度球-1.3.0-安装版.exe'))