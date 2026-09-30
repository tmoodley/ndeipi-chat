<#
.SYNOPSIS
    Packs the micro-app SDK: NdeipiChat.SubApps.Sdk, NdeipiChat.SubApps.DevHost and
    NdeipiChat.SubApps.Templates, into one folder that works as a NuGet feed.

.EXAMPLE
    ./eng/pack-subapps-sdk.ps1
    dotnet new install ./artifacts/packages/NdeipiChat.SubApps.Templates.0.1.0.nupkg
    dotnet nuget add source "$PWD/artifacts/packages" --name ndeipi-local
#>
param(
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\packages'),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$projects = @(
    'src\NdeipiChat.SubApps.Sdk\NdeipiChat.SubApps.Sdk.csproj',
    'src\NdeipiChat.SubApps.DevHost\NdeipiChat.SubApps.DevHost.csproj',
    'templates\NdeipiChat.SubApps.Templates\NdeipiChat.SubApps.Templates.csproj'
)

New-Item -ItemType Directory -Force $Output | Out-Null
foreach ($project in $projects) {
    dotnet pack (Join-Path $root $project) -c $Configuration -o $Output
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
}

Write-Host ''
Write-Host "Packages are in $(Resolve-Path $Output)"
