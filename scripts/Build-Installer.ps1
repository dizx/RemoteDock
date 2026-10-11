[CmdletBinding()]
param(
    [string] $IsccPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root 'artifacts\publish\win-x64'

if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $IsccPath = $command.Source
    }
    else {
        $candidates = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )
        $IsccPath = $candidates | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
    }
}

if (-not $IsccPath -or -not (Test-Path $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isinfo.php or supply -IsccPath.'
}

[xml] $properties = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = [string] $properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Directory.Build.props must specify a three-part numeric Version.'
}

Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'Generate-RdpInterop.ps1')

    # Clear previous publish output so removed files cannot enter the installer.
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    dotnet publish src/RemoteDock.App/RemoteDock.App.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    & $IsccPath "/DAppVersion=$version" "/DPublishDir=$publishDir" (Join-Path $root 'installer\RemoteDock.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

    $installer = Join-Path $root "artifacts\installer\RemoteDock-$version-win-x64-Setup.exe"
    if (-not (Test-Path $installer -PathType Leaf)) {
        throw "Installer was not created: $installer"
    }
    Write-Output "Installer created: $installer"
}
finally {
    Pop-Location
}
