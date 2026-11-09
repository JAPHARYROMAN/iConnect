# Shared validation for pinned native source corrections. Dot-source only.
$script:IConnectNativePatchTools = $PSScriptRoot

function Get-IConnectNativeInputHash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-IConnectNativeTextHash([string]$Path) {
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
}

function Get-IConnectNativePatchDefinitionName([string]$Id) {
    switch -CaseSensitive ($Id) {
        'serialize-sctp-initialization-v1' { return 'native-sctp-initialization.json' }
        'initialize-dtls-before-receive-v1' { return 'native-dtls-initialization.json' }
        default { throw 'Native patch evidence differs from the pinned correction.' }
    }
}

function Get-IConnectNativePatchDefinition([string]$Id) {
    $path = Join-Path $script:IConnectNativePatchTools (Get-IConnectNativePatchDefinitionName $Id)
    if ((Get-Item -LiteralPath $path).Length -gt 16KB) { throw 'Native patch definition is oversized.' }
    $patch = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $paths = if ($Id -ceq 'serialize-sctp-initialization-v1') { @('src/impl/peerconnection.cpp','src/impl/peerconnection.hpp') } else { @('src/impl/dtlstransport.cpp') }
    $file = if ($Id -ceq 'serialize-sctp-initialization-v1') { 'native-patches/serialize-sctp-initialization.patch' } else { 'native-patches/initialize-dtls-before-receive.patch' }
    if ($patch.Count -ne 6 -or $patch.schema -ne 1 -or $patch.id -cne $Id -or
        $patch.baseCommit -cne '6b1e2e620f1e37f0eafeee702eaea0043cb305fd' -or
        $patch.patchFile -cne $file -or
        $patch.patchSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $patch.postImages -isnot [Collections.IDictionary] -or
        $patch.postImages.Count -ne @($paths).Count -or @($paths | Where-Object { !$patch.postImages.Contains($_) -or $patch.postImages[$_] -cnotmatch '\A[a-f0-9]{64}\z' }).Count) {
        throw 'Unsupported native patch definition.'
    }
    $patchPath = Join-Path $script:IConnectNativePatchTools $patch.patchFile
    if ((Get-Item -LiteralPath $patchPath).Length -gt 64KB -or (Get-IConnectNativeInputHash $patchPath) -cne $patch.patchSha256) {
        throw 'Native patch bytes differ from their pinned definition.'
    }
    return $patch
}

function Get-IConnectNativeSctpPatch { Get-IConnectNativePatchDefinition 'serialize-sctp-initialization-v1' }
function Get-IConnectNativeDtlsPatch { Get-IConnectNativePatchDefinition 'initialize-dtls-before-receive-v1' }

function Get-IConnectNativeSourcePatches([Collections.IDictionary]$Evidence) {
    if (!$Evidence.Contains('nativePatches') -or $null -eq $Evidence.nativePatches -or @($Evidence.nativePatches).Count -eq 0) { return }
    $records = @($Evidence.nativePatches)
    if ($records.Count -gt 2 -or @($records | Where-Object { $_ -isnot [Collections.IDictionary] }).Count) { throw 'Unsupported native patch inventory.' }
    $ids = @($records | ForEach-Object { $_.id })
    if ($records.Count -eq 2 -and ($ids[0] -cne 'serialize-sctp-initialization-v1' -or $ids[1] -cne 'initialize-dtls-before-receive-v1')) { throw 'Unsupported native patch inventory or order.' }
    if ($records.Count -eq 1 -and $ids[0] -ceq 'initialize-dtls-before-receive-v1') { throw 'The DTLS correction requires the SCTP correction.' }
    foreach ($record in $records) {
        $expected = Get-IConnectNativePatchDefinition $record.id
        if ($record.Count -ne $expected.Count) { throw 'Native patch evidence has unexpected fields.' }
        foreach ($key in @('schema','id','baseCommit','patchFile','patchSha256')) {
            if ($record[$key] -cne $expected[$key]) { throw 'Native patch evidence differs from the pinned correction.' }
        }
        if ($record.postImages -isnot [Collections.IDictionary] -or $record.postImages.Count -ne $expected.postImages.Count) { throw 'Native patched source hashes are incomplete.' }
        foreach ($name in $expected.postImages.Keys) {
            if ($record.postImages[$name] -cne $expected.postImages[$name]) { throw 'Native patched source hashes differ.' }
        }
        Write-Output $expected
    }
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
    $images = @{}
    foreach ($patch in $expected) {
        if ($patch.baseCommit -cne $Commit) { throw 'Native patch does not target this source revision.' }
        foreach ($name in $patch.postImages.Keys) {
            if ($images.ContainsKey($name)) { throw 'Overlapping native source patches are unsupported.' }
            $images[$name] = $patch.postImages[$name]
        }
    }
    $wanted = @($images.Keys | ForEach-Object { ' M ' + $_ })
    if ($changes.Count -ne $wanted.Count -or @($changes | Where-Object { $_ -cnotin $wanted }).Count) {
        throw 'Native source changes exceed the pinned patch.'
    }
    foreach ($name in $images.Keys) {
        $file = Join-Path $Path $name
        $item = Get-Item -LiteralPath $file -Force
        if ($item.PSIsContainer -or $item.Length -gt 1MB -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (Get-IConnectNativeTextHash $file) -cne $images[$name]) { throw 'Native patched source content differs.' }
    }
}

function Initialize-IConnectNativeSourcePatch([string]$Path,[string]$Commit,[object[]]$Patches) {
    $expected = @(Get-IConnectNativeSourcePatches @{nativePatches=$Patches})
    if (!$expected.Count -or @($expected | Where-Object { $_.baseCommit -cne $Commit }).Count) { throw 'Native patch source revision differs.' }
    $changes = @(Invoke-IConnectNativePatchGit -Arguments @('-C',$Path,'status','--porcelain','--untracked-files=all'))
    if ($changes.Count -eq 0) {
        Assert-IConnectNativeSource $Path $Commit
        foreach ($patch in $expected) {
            $patchPath = Join-Path $script:IConnectNativePatchTools $patch.patchFile
            [void](Invoke-IConnectNativePatchGit -Arguments @('-C',$Path,'apply','--check',$patchPath))
            [void](Invoke-IConnectNativePatchGit -Arguments @('-C',$Path,'apply',$patchPath))
        }
    }
    Assert-IConnectNativeSource $Path $Commit $expected
}

function Assert-IConnectNativePatchArtifacts([string]$Artifact,[object[]]$Patches) {
    foreach ($patch in $Patches) {
        $definition = Get-IConnectNativePatchDefinitionName $patch.id
        $path = Join-Path $Artifact $patch.patchFile
        if ((Get-IConnectNativeInputHash $path) -cne $patch.patchSha256 -or
            (Get-IConnectNativeInputHash (Join-Path $Artifact $definition)) -cne
            (Get-IConnectNativeInputHash (Join-Path $script:IConnectNativePatchTools $definition))) {
            throw 'Native artifact is missing its exact patch inputs.'
        }
    }
}
