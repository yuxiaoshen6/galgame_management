[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src/GalgameManager.Desktop/GalgameManager.Desktop.csproj'
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
$releaseName = "GalgameManager-$version-win-x64"
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$staging = [System.IO.Path]::GetFullPath((Join-Path $artifacts ('.publish-' + [guid]::NewGuid().ToString('N'))))
$archive = Join-Path $artifacts "$releaseName.zip"

New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited with code $LASTEXITCODE." }
    Copy-Item -LiteralPath (Join-Path $repository 'README.md'), (Join-Path $repository 'CHANGELOG.md') -Destination $staging
    Copy-Item -LiteralPath (Join-Path $repository 'docs') -Destination $staging -Recurse
    $temporaryArchive = Join-Path $artifacts ('.archive-' + [guid]::NewGuid().ToString('N') + '.zip')
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $temporaryArchive -CompressionLevel Optimal
    Move-Item -LiteralPath $temporaryArchive -Destination $archive -Force
    Get-FileHash -Algorithm SHA256 -LiteralPath $archive
}
finally {
    if ([System.IO.Path]::GetDirectoryName($staging) -eq $artifacts -and
        [System.IO.Path]::GetFileName($staging) -match '^\.publish-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    if ($temporaryArchive -and (Test-Path -LiteralPath $temporaryArchive)) {
        Remove-Item -LiteralPath $temporaryArchive -Force
    }
}
