[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/portable')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new output directory for each portable build.' }
Push-Location $projectRoot
try {
    dotnet publish src/BrimDeck/BrimDeck.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
    [IO.File]::WriteAllText((Join-Path $outputPath 'portable.flag'), 'BrimDeck portable market edition', [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath docs/market-feed.example.json -Destination (Join-Path $outputPath 'market-feed.example.json')
    Copy-Item -LiteralPath docs/market-portable.zh-CN.txt -Destination (Join-Path $outputPath '使用说明.txt')
    if (!(Test-Path -LiteralPath (Join-Path $outputPath 'BrimDeck.exe'))) { throw 'Portable executable missing.' }
    if (Get-ChildItem -LiteralPath $outputPath -File | Where-Object Name -Match '(?i)(setup|install).*\.exe$') { throw 'An installer must not be included.' }
    $manifest = Get-ChildItem -LiteralPath $outputPath -File | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    $manifest | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding utf8
}
finally { Pop-Location }
