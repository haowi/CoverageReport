# SPDX-FileCopyrightText: 2026 Hanno Witt
# SPDX-License-Identifier: Apache-2.0

<#
.SYNOPSIS
Writes a CycloneDX 1.6 SBOM for dist\coveragereport.exe.

.DESCRIPTION
The SBOM describes the exe and the .NET components built into it: the trimmed runtime from the package
Microsoft.NETCore.App.Runtime.win-x64 and the single-file host from Microsoft.NETCore.App.Host.win-x64.

Every value comes from the build that produced the exe: the runtime version from the deps.json written by the publish,
package metadata from the local NuGet cache, the host from the SDK's packs folder, and the hashes from the files
themselves. Nothing is downloaded. build.ps1 runs this script after every successful publish.

Each run creates a new BOM, so serial number and timestamp change even when the exe does not, as CycloneDX intends.
#>
[CmdletBinding()]
param(
    [string] $Exe = (Join-Path $PSScriptRoot '..\dist\coveragereport.exe'),
    [string] $DepsJson = (Join-Path $PSScriptRoot '..\src\bin\Release\net10.0\win-x64\coveragereport.deps.json'),
    [string[]] $OutFile = @(
        (Join-Path $PSScriptRoot '..\dist\coveragereport.cdx.json'),
        (Join-Path $PSScriptRoot '..\sbom\coveragereport.cdx.json'))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RuntimePackage = 'Microsoft.NETCore.App.Runtime.win-x64'
$HostPackage = 'Microsoft.NETCore.App.Host.win-x64'
$Author = 'Hanno Witt'

function Get-Hashes([string] $Path) {
    @('SHA256', 'SHA512') | ForEach-Object {
        [ordered]@{ alg = $_.Insert(3, '-'); content = (Get-FileHash -LiteralPath $Path -Algorithm $_).Hash.ToLowerInvariant() }
    }
}

# A single SPDX identifier is written as an id, anything else (such as "MIT OR Apache-2.0") as an expression.
function Get-License([string] $Expression) {
    if ($Expression -match '^[A-Za-z0-9.+-]+$') { [ordered]@{ license = [ordered]@{ id = $Expression } } }
    else { [ordered]@{ expression = $Expression } }
}

# Serializes ordered dictionaries, arrays, strings and numbers as JSON with two-space indentation. ConvertTo-Json of
# Windows PowerShell 5.1 produces valid but oddly indented output, and escapes characters such as < and '.
function ConvertTo-IndentedJson($Value, [string] $Indent = '') {
    $inner = $Indent + '  '
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [string]) {
        $sb = New-Object System.Text.StringBuilder
        foreach ($c in $Value.ToCharArray()) {
            switch ($c) {
                '"' { [void]$sb.Append('\"') }
                '\' { [void]$sb.Append('\\') }
                default {
                    if ([int]$c -lt 0x20) { [void]$sb.AppendFormat('\u{0:x4}', [int]$c) } else { [void]$sb.Append($c) }
                }
            }
        }
        return '"' + $sb.ToString() + '"'
    }
    if ($Value -is [bool]) { return $Value.ToString().ToLowerInvariant() }
    if ($Value -is [int] -or $Value -is [long]) { return $Value.ToString([Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [Collections.IDictionary]) {
        if ($Value.Count -eq 0) { return '{}' }
        $members = foreach ($key in $Value.Keys) { "$inner$(ConvertTo-IndentedJson ([string]$key)): $(ConvertTo-IndentedJson $Value[$key] $inner)" }
        return "{`n" + ($members -join ",`n") + "`n$Indent}"
    }
    if ($Value -is [Collections.IEnumerable]) {
        $items = @(foreach ($item in $Value) { "$inner$(ConvertTo-IndentedJson $item $inner)" })
        if ($items.Count -eq 0) { return '[]' }
        return "[`n" + ($items -join ",`n") + "`n$Indent]"
    }
    throw "Cannot serialize a value of type $($Value.GetType().FullName)."
}

# ---- the exe ----

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "Executable not found: $Exe" }
$info = (Get-Item -LiteralPath $Exe).VersionInfo
$version = $info.ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected product version '$version' in $Exe." }
$appRef = "pkg:generic/coveragereport@$version"

# ---- the .NET runtime built into the exe ----

if (-not (Test-Path -LiteralPath $DepsJson -PathType Leaf)) { throw "deps.json of the publish not found: $DepsJson" }
$deps = Get-Content -LiteralPath $DepsJson -Raw -Encoding UTF8 | ConvertFrom-Json
$runtimeEntry = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like "runtimepack.$RuntimePackage/*" })
if ($runtimeEntry.Count -ne 1) { throw "The deps.json does not name exactly one $RuntimePackage." }
$runtimeVersion = $runtimeEntry[0].Split('/')[1]

$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$runtimeDir = Join-Path $packages "$($RuntimePackage.ToLowerInvariant())\$runtimeVersion"
$nupkg = Join-Path $runtimeDir "$($RuntimePackage.ToLowerInvariant()).$runtimeVersion.nupkg"
# Loaded as XML rather than as text, so the declared encoding is honored (Get-Content of PowerShell 5.1 would read the
# UTF-8 file as ANSI and garble the copyright sign). DTDs are not processed.
$nuspec = New-Object System.Xml.XmlDocument
$nuspec.XmlResolver = $null
$nuspec.Load((Join-Path $runtimeDir "$($RuntimePackage.ToLowerInvariant()).nuspec"))
$meta = $nuspec.package.metadata
$commit = $meta.repository.commit

$runtimeRef = "pkg:nuget/$RuntimePackage@$runtimeVersion"
$supplier = [ordered]@{ name = 'Microsoft Corporation'; url = @($meta.projectUrl) }
$dotnetReferences = @(
    [ordered]@{ type = 'website'; url = $meta.projectUrl },
    [ordered]@{ type = 'vcs'; url = "$($meta.repository.url)/tree/$commit"; comment = "Source of this .NET release, commit $commit" },
    [ordered]@{ type = 'release-notes'; url = $meta.releaseNotes })

$runtime = [ordered]@{
    type               = 'framework'
    'bom-ref'          = $runtimeRef
    supplier           = $supplier
    name               = $RuntimePackage
    version            = $runtimeVersion
    description        = '.NET runtime, built into the exe in trimmed form. Its third-party notices are in THIRD-PARTY-NOTICES.TXT of the package.'
    hashes             = @(Get-Hashes $nupkg)
    licenses           = @(Get-License $meta.license.'#text')
    copyright          = $meta.copyright
    purl               = $runtimeRef
    externalReferences = $dotnetReferences
}

# ---- the single-file host, the native part of the exe ----

# The SDK takes the host from the same .NET release as the runtime; package metadata is shared with the runtime package,
# because installed SDK packs carry no nuspec of their own.
$dotnetRoot = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Split-Path (Get-Command dotnet).Source }
$singleFileHost = Join-Path $dotnetRoot "packs\$HostPackage\$runtimeVersion\runtimes\win-x64\native\singlefilehost.exe"
if (-not (Test-Path -LiteralPath $singleFileHost -PathType Leaf)) { throw "Single-file host not found: $singleFileHost" }

$hostRef = "pkg:nuget/$HostPackage@$runtimeVersion"
$singleFile = [ordered]@{
    type               = 'application'
    'bom-ref'          = $hostRef
    supplier           = $supplier
    name               = $HostPackage
    version            = $runtimeVersion
    description        = 'singlefilehost.exe: starts the built-in runtime and forms the native part of the exe. The hashes are those of the host before the application was bundled into it.'
    hashes             = @(Get-Hashes $singleFileHost)
    licenses           = @(Get-License $meta.license.'#text')
    copyright          = $meta.copyright
    purl               = $hostRef
    externalReferences = $dotnetReferences
}

# ---- the BOM ----

$application = [ordered]@{
    type        = 'application'
    'bom-ref'   = $appRef
    supplier    = [ordered]@{ name = $info.CompanyName }
    authors     = @([ordered]@{ name = $Author })
    name        = $info.ProductName
    version     = $version
    description = 'Renders C/C++ and assembler code coverage as an HTML report. Distributed as the single file coveragereport.exe.'
    hashes      = @(Get-Hashes $Exe)
    licenses    = @(Get-License 'Apache-2.0')
    copyright   = $info.LegalCopyright
    purl        = $appRef
}

$bom = [ordered]@{
    '$schema'    = 'http://cyclonedx.org/schema/bom-1.6.schema.json'
    bomFormat    = 'CycloneDX'
    specVersion  = '1.6'
    serialNumber = "urn:uuid:$([guid]::NewGuid())"
    version      = 1
    metadata     = [ordered]@{
        timestamp    = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
        lifecycles   = @([ordered]@{ phase = 'build' })
        tools        = [ordered]@{
            components = @([ordered]@{
                type        = 'application'
                name        = 'New-Sbom.ps1'
                version     = $version
                description = 'SBOM generator of the Coverage Report build'
            })
        }
        authors      = @([ordered]@{ name = $Author })
        manufacturer = [ordered]@{ name = $Author }
        supplier     = [ordered]@{ name = $Author }
        component    = $application
    }
    components   = @($runtime, $singleFile)
    dependencies = @(
        [ordered]@{ ref = $appRef; dependsOn = @($runtimeRef, $hostRef) },
        [ordered]@{ ref = $runtimeRef; dependsOn = @() },
        [ordered]@{ ref = $hostRef; dependsOn = @() })
}

$json = ConvertTo-IndentedJson $bom
$utf8 = New-Object System.Text.UTF8Encoding $false
foreach ($file in $OutFile) {
    $full = [IO.Path]::GetFullPath($file)
    $folder = Split-Path $full
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }
    [IO.File]::WriteAllText($full, $json + "`n", $utf8)
    Write-Host "SBOM: $full"
}
