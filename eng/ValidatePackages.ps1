param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @(Get-ChildItem $Directory -Filter '*.nupkg')
if ($packages.Count -ne 1) { throw "Expected exactly one nupkg, found $($packages.Count)." }
$expected = "ModelArtifacts.NET.$Version.nupkg"
if ($packages[0].Name -ne $expected) { throw "Expected $expected but found $($packages[0].Name)." }
$zip = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $names = @($zip.Entries | ForEach-Object FullName)
    foreach ($required in @('README.md', '128x128_compressed.png', 'LICENSE')) {
        if ($names -notcontains $required) { throw "Package is missing $required." }
    }
    if (-not ($names | Where-Object { $_ -like 'lib/net10.0/ModelArtifacts.NET.dll' })) { throw 'Package is missing the net10.0 library.' }
    if (-not ($names | Where-Object { $_ -like '*.nuspec' })) { throw 'Package is missing its nuspec.' }
}
finally { $zip.Dispose() }
Write-Host "Validated $expected"
