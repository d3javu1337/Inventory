function Get-BackupLayout([string]$BackupPath, [switch]$VerifyHashes) {
    $source = (Resolve-Path -LiteralPath $BackupPath).Path
    function Get-BackupItem([string]$Path, [bool]$Directory) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.PSIsContainer -ne $Directory -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected backup entry: $Path"
        }
        return $item
    }
    $null = Get-BackupItem $source $true
    $manifestFile = Get-BackupItem (Join-Path $source 'manifest.json') $false
    $database = Get-BackupItem (Join-Path $source 'database.dump') $false
    $documents = Get-BackupItem (Join-Path $source 'documents') $true
    $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    if ($manifest.version -ne 1) { throw 'Unsupported backup version.' }

    $paths = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $paths.Add('database.dump', $database.FullName)
    foreach ($item in Get-ChildItem -LiteralPath $documents.FullName -Force -Recurse) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected backup link: $($item.Name)" }
        if ($item.PSIsContainer) { continue }
        $relative = [IO.Path]::GetRelativePath($source, $item.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
        if ($relative -match '[\x00-\x1f\\]') { throw 'Invalid backup file name.' }
        $paths.Add($relative, $item.FullName)
    }

    $hashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $manifest.files) {
        $path = [string]$entry.path
        $hash = [string]$entry.sha256
        if (!$paths.ContainsKey($path) -or $hash -notmatch '^[0-9a-f]{64}$' -or !$hashes.TryAdd($path, $hash)) {
            throw "Invalid or duplicate manifest entry: $path"
        }
        if ($VerifyHashes -and (Get-FileHash -LiteralPath $paths[$path] -Algorithm SHA256).Hash -ne $hash) {
            throw "Hash mismatch: $path"
        }
    }
    if ($hashes.Count -ne $paths.Count) { throw 'Backup contains files missing from the manifest.' }
    return [pscustomobject]@{ Manifest = $manifestFile.FullName; Paths = $paths; Hashes = $hashes }
}
