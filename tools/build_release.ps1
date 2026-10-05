param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$OutputDirectory,
    [string]$Version = '0.1.1-alpha'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if(-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'releases' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Push-Location $root
try {
    & python tools/catalog.py validate .
    if($LASTEXITCODE -ne 0) { throw 'Translation validation failed' }
    & dotnet run --project tests/CoreTests.csproj -- .
    if($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    & dotnet build src/Plugin/TinyRogues.Chinese.csproj -c Release --nologo "-p:GameDir=$GameDir"
    if($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
    $stage = Join-Path $destination ('TinyRogues-Chinese-' + $Version)
    $plugin = Join-Path $stage 'TinyRogues.Chinese'
    if(Test-Path -LiteralPath $stage) { throw "Stage already exists; choose a fresh output directory: $stage" }
    New-Item -ItemType Directory -Path (Join-Path $plugin 'locales') -Force | Out-Null
    Copy-Item -LiteralPath 'src/Plugin/bin/Release/netstandard2.1/TinyRogues.Chinese.dll' -Destination $plugin
    Copy-Item -LiteralPath 'locales/zh-Hans' -Destination (Join-Path $plugin 'locales') -Recurse
    New-Item -ItemType Directory -Path (Join-Path $plugin 'diagnostics') | Out-Null
    Get-ChildItem -LiteralPath 'tests/fixtures' -File -Filter '*.json' |
        Copy-Item -Destination (Join-Path $plugin 'diagnostics')
    Copy-Item -LiteralPath 'examples' -Destination $plugin -Recurse
    Copy-Item -LiteralPath 'README.md','LICENSE','CONTRIBUTING.md' -Destination $stage
    Copy-Item -LiteralPath 'docs' -Destination $stage -Recurse
    $dll = Join-Path $plugin 'TinyRogues.Chinese.dll'
    @{version=$Version; pluginId='community.tinyrogues.chinese'; dllSha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8
    $archive = Join-Path $destination ('TinyRogues-Chinese-' + $Version + '.zip')
    if(Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }
    Compress-Archive -LiteralPath $stage -DestinationPath $archive
    Write-Output $archive
} finally { Pop-Location }
