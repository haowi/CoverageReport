# SPDX-FileCopyrightText: 2026 Hanno Witt
# SPDX-License-Identifier: Apache-2.0

# Builds dist\coveragereport.exe as a single self-contained file that needs no .NET installation on the target machine,
# and writes its CycloneDX SBOM next to it and to sbom\coveragereport.cdx.json.
# This build is the only step that accesses the internet: it downloads .NET's runtime pack from nuget.org once.
# Coverage Report itself never does.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
dotnet publish "$PSScriptRoot\src" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true `
    -o "$PSScriptRoot\dist"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

try {
    & "$PSScriptRoot\tools\New-Sbom.ps1"
}
catch {
    Write-Error "SBOM not written: $_"
    exit 1
}
exit 0
