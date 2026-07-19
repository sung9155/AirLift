# Builds AirLift.msi (self-contained x64, no .NET runtime required on target).
# Prereq: dotnet tool install --global wix
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

dotnet publish $root -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$PSScriptRoot\publish"

Push-Location $PSScriptRoot
try {
    wix extension add WixToolset.Firewall.wixext/4.0.5
    wix build Package.wxs -ext WixToolset.Firewall.wixext/4.0.5 -arch x64 -o AirLift.msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
    Write-Host "OK: $PSScriptRoot\AirLift.msi"
}
finally { Pop-Location }
