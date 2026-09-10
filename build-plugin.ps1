$ErrorActionPreference = 'Stop'

$pluginRoot = $PSScriptRoot
$webRoot = Join-Path $pluginRoot '..\WebEasiNote'

pnpm --dir $webRoot build
dotnet clean (Join-Path $pluginRoot 'ICC.CE.ENBX.csproj') -c Release
dotnet build (Join-Path $pluginRoot 'ICC.CE.ENBX.csproj') -c Release
