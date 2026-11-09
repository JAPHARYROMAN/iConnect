# Shared validation for the single reviewed native source patch. Dot-source only.
$script:IConnectNativePatchTools = $PSScriptRoot

function Get-IConnectNativeInputHash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-IConnectNativeTextHash([string]$Path) {
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
}

function Get-IConnectNativeSctpPatch {
    $path = Join-Path $script:IConnectNativePatchTools 'native-sctp-initialization.json'
    if ((Get-Item -LiteralPath $path).Length -gt 16KB) { throw 'Native patch definition is oversized.' }
    $patch = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $paths = @('src/impl/peerconnection.cpp','src/impl/peerconnection.hpp')
    if ($patch.Count -ne 6 -or $patch.schema -ne 1 -or $patch.id -cne 'serialize-sctp-initialization-v1' -or
        $patch.baseCommit -cne '6b1e2e620f1e37f0eafeee702eaea0043cb305fd' -or
        $patch.patchFile -cne 'native-patches/serialize-sctp-initialization.patch' -or
        $patch.patchSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $patch.postImages -isnot [Collections.IDictionary] -or
        $patch.postImages.Count -ne 2 -or @($paths | Where-Object { !$patch.postImages.Contains($_) -or $patch.postImages[$_] -cnotmatch '\A[a-f0-9]{64}\z' }).Count) {
        throw 'Unsupported native patch definition.'
    }
    $patchPath = Join-Path $script:IConnectNativePatchTools $patch.patchFile
    if ((Get-Item -LiteralPath $patchPath).Length -gt 64KB -or (Get-IConnectNativeInputHash $patchPath) -cne $patch.patchSha256) {
        throw 'Native patch bytes differ from their pinned definition.'
    }
    return $patch
}

function Get-IConnectNativeSourcePatches([Collections.IDictionary]$Evidence) {
    if (!$Evidence.Contains('nativePatches') -or $null -eq $Evidence.nativePatches -or @($Evidence.nativePatches).Count -eq 0) { return }
    $records = @($Evidence.nativePatches)
    if ($records.Count -ne 1 -or $records[0] -isnot [Collections.IDictionary]) { throw 'Unsupported native patch inventory.' }
    $expected = Get-IConnectNativeSctpPatch
    $record = $records[0]
    if ($record.Count -ne $expected.Count) { throw 'Native patch evidence has unexpected fields.' }
    foreach ($key in @('schema','id','baseCommit','patchFile','patchSha256')) {
        if ($record[$key] -cne $expected[$key]) { throw 'Native patch evidence differs from the pinned correction.' }
    }
    if ($record.postImages -isnot [Collections.IDictionary] -or $record.postImages.Count -ne 2) { throw 'Native patched source hashes are incomplete.' }
    foreach ($name in $expected.postImages.Keys) {
        if ($record.postImages[$name] -cne $expected.postImages[$name]) { throw 'Native patched source hashes differ.' }
    }
    return $expected
}

function Invoke-IConnectNativePatchGit([string[]]$Arguments) {
    $output = & git.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Native patch source validation failed (git exit $LASTEXITCODE)." }
    return $output
}

function Assert-IConnectNativeSource([string]$Path,[string]$Commit,[object[]]$Patches = @()) {
    if ($Commit -cnotmatch '\A[a-f0-9]{40}\z' -or
        (Invoke-IConnectNativePatchGit -Arguments @('-C',$Path,'rev-parse','HEAD')).Trim() -cne $Commit) {
        throw 'Native source revision differs from its pin.'
    }
    $changes = @(Invoke-IConnectNativePatchGit -Arguments @('-C',$Path,'status','--porcelain','--untracked-files=all'))
    if ($Patches.Count -eq 0) {
        if ($changes.Count) { throw "Native source has local changes: $Path" }
        return
    }
    $expected = @(Get-IConnectNativeSourcePatches @{nativePatches=$Patches})
    if ($expected.Count -ne 1 -or $expected[0].baseCommit -cne $Commit) { throw 'Native patch does not target this source revision.' }
    $patch = $expected[0]
    $wanted = @($patch.postImages.Keys | ForEach-Object { ' M ' + $_ })
    if ($changes.Count -ne $wanted.Count -or @($changes | Where-Object { $_ -cnotin $wanted }).Count) {
        throw 'Native source changes exceed the pinned patch.'
    }
    foreach ($name in $patch.postImages.Keys) {
        $file = Join-Path $Path $name
        $item = Get-Item -LiteralPath $file -Force
        if ($item.PSIsContainer -or $item.Length -gt 1MB -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (Get-IConnectNativeTextHash $file) -cne $patch.postImages[$name]) { throw 'Native patched source content differs.' }
    }
}

function Initialize-IConnectNativeSourcePatch([string]$Path,[string]$Commit,[object[]]$Patches) {
    $expected = @(Get-IConnectNativeSourcePatches @{nativePatches=$Patches})
    if ($expected.Count -ne 1 -or $expected[0].baseCommit -cne $Commit) { throw 'Native patch source revision differs.' }
    $changes = @(Invoke-IConnectNativePatchGit -Arguments @('-C',$Path,'status','--porcelain','--untracked-files=all'))
    if ($changes.Count -eq 0) {
        Assert-IConnectNativeSource $Path $Commit
        $patchPath = Join-Path $script:IConnectNativePatchTools $expected[0].patchFile
        [void](Invoke-IConnectNativePatchGit -Arguments @('-C',$Path,'apply','--check',$patchPath))
        [void](Invoke-IConnectNativePatchGit -Arguments @('-C',$Path,'apply',$patchPath))
    }
    Assert-IConnectNativeSource $Path $Commit $expected
}

function Assert-IConnectNativePatchArtifacts([string]$Artifact,[object[]]$Patches) {
    foreach ($patch in $Patches) {
        $path = Join-Path $Artifact $patch.patchFile
        if ((Get-IConnectNativeInputHash $path) -cne $patch.patchSha256 -or
            (Get-IConnectNativeInputHash (Join-Path $Artifact 'native-sctp-initialization.json')) -cne
            (Get-IConnectNativeInputHash (Join-Path $script:IConnectNativePatchTools 'native-sctp-initialization.json'))) {
            throw 'Native artifact is missing its exact patch inputs.'
        }
    }
}
