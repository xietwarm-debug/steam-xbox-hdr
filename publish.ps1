param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$taskSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
$taskOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$taskPublish = Join-Path $taskOutput 'SteamXboxHdr-win-x64'
New-Item -ItemType Directory -Force -Path $taskPublish | Out-Null
& $taskSdk publish (Join-Path $PSScriptRoot 'src\SteamXboxHdr\SteamXboxHdr.csproj') -c Release -r win-x64 --self-contained true -o $taskPublish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $taskPublish '使用说明.md') -Force
$taskArchive = Join-Path $taskOutput 'SteamXboxHdr-win-x64.zip'
Compress-Archive -LiteralPath $taskPublish -DestinationPath $taskArchive -Force
Get-Item -LiteralPath $taskArchive | Select-Object FullName,Length
