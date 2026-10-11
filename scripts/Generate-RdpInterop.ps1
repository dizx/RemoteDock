[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\interop'
$typeLibrary = Join-Path $env:WINDIR 'System32\mstscax.dll'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows'
$aximp = Get-ChildItem $sdkRoot -Filter AxImp.exe -Recurse |
    Where-Object { $_.Directory.Name -eq 'x64' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $aximp) {
    throw 'AxImp.exe was not found. Install the .NET Framework 4.8 SDK tools using Visual Studio Installer.'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $output
try {
    & $aximp.FullName $typeLibrary '/out:AxMSTSCLib.dll' '/silent'
    if ($LASTEXITCODE -ne 0) { throw "AxImp failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
