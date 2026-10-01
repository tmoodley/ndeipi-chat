<#
.SYNOPSIS
    Builds the Windows installer, artifacts\Ndeipi-<version>-x64.msi: publishes the desktop app
    (src/NdeipiChat.Desktop) self-contained, then packs it with WiX (installer\NdeipiChat.Desktop.Installer).

.EXAMPLE
    ./eng/build-desktop-msi.ps1
    ./eng/build-desktop-msi.ps1 -Version 1.0.3
#>
param(
    # Windows Installer versions are major.minor.build; a higher one upgrades an installed lower one.
    [string] $Version = '1.0.0',
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $Output 'Ndeipi-Windows-x64'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be major.minor.build, e.g. 1.0.3." }

dotnet publish (Join-Path $root 'src\NdeipiChat.Desktop') -f net10.0-windows10.0.19041.0 -c Release -p:SelfContained=true `
    -p:ApplicationDisplayVersion=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publishing the desktop app failed.' }

dotnet build (Join-Path $root 'installer\NdeipiChat.Desktop.Installer\NdeipiChat.Desktop.Installer.wixproj') -c Release `
    -p:AppPublishDir="$((Resolve-Path $publish).Path)\" -p:ProductVersion=$Version -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }

$msi = Join-Path $Output "Ndeipi-$Version-x64.msi"
Write-Host ''
Write-Host "Installer: $((Resolve-Path $msi).Path) ($([math]::Round((Get-Item $msi).Length / 1MB)) MB)"
