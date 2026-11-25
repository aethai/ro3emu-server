# The stamp of the hooks and loc sources (as git hashes them) and the SHA-256 of the committed prebuilt DLL bytes.
# prebuilt/client/source.sha256 holds both so neither stale sources nor tampered binaries can ship.
#
# usage: pwsh tools/release/client-stamp.ps1          prints the current source + DLL stamp
#        pwsh tools/release/client-stamp.ps1 -Check   fails when sources or DLL bytes differ from source.sha256
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$prebuiltDir = Join-Path $root 'prebuilt\client'

function Get-Sha256Hex([byte[]]$bytes) {
    $alg = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($alg.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $alg.Dispose() }
}

Push-Location $root
try {
    $files = @(git ls-files src/Ro3Emu.ClientHooks src/Ro3Emu.Loc | Where-Object { $_ -notmatch '/(bin|obj)/' })
    if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw "git ls-files failed" }
    $hashes = @(git hash-object -- @files)
    if ($LASTEXITCODE -ne 0 -or $hashes.Count -ne $files.Count) { throw "git hash-object failed" }
    $text = (0..($files.Count - 1) | ForEach-Object { "$($files[$_]) $($hashes[$_])" }) -join "`n"
    $sourceHash = Get-Sha256Hex ([Text.Encoding]::UTF8.GetBytes($text))
} finally { Pop-Location }

$dlls = @(Get-ChildItem -LiteralPath $prebuiltDir -Filter '*.dll' -File | Sort-Object Name)
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("sources $sourceHash")
foreach ($dll in $dlls) {
    $dllHash = Get-Sha256Hex ([IO.File]::ReadAllBytes($dll.FullName))
    $lines.Add("$($dll.Name) $dllHash")
}
$current = ($lines -join "`n") + "`n"

if (-not $Check) { return $current }
$stampPath = Join-Path $prebuiltDir 'source.sha256'
$recorded = ((Get-Content -LiteralPath $stampPath -Raw).Replace("`r`n", "`n").TrimEnd("`n")) + "`n"
if ($recorded -ne $current) {
    throw "prebuilt/client does not match current hooks/loc sources or DLL hashes.`nRecorded:`n$recorded`nCurrent:`n$current`nOn a machine with the current client run: pwsh tools/release/pack.ps1 -UpdatePrebuiltClientDlls, then commit prebuilt/client."
}
Write-Host "prebuilt/client matches hooks/loc sources and DLL hashes ($sourceHash)"
