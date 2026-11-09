#Requires -Version 7.2
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$names=@('Habari-文書.txt','salama-é.txt','safari-😀.txt')
$executable=(Get-Process -Id $PID).Path
$childCode=@'
$ErrorActionPreference='Stop'
[Console]::InputEncoding=[Text.Encoding]::GetEncoding(437)
$value=ConvertFrom-Json -InputObject ([Console]::In.ReadLine())
foreach($name in $value.names) {([int[]][char[]]$name) -join ','}
'@
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCode))
function Observe-Names([string]$Driver) {
    $errors=$null
    $tree=[Management.Automation.Language.Parser]::ParseFile($Driver,[ref]$null,[ref]$errors)
    if($errors) {throw 'The native runner does not parse.'}
    $functions=@($tree.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Host-Command'},$true))
    if($functions.Count -ne 1) {throw 'Expected exactly one native host command writer.'}
    . ([scriptblock]::Create($functions[0].Extent.Text))
    $info=[Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $info.StandardInputEncoding=[Text.Encoding]::GetEncoding(437)
    foreach($argument in @('-NoProfile','-NonInteractive','-EncodedCommand',$encoded)) {[void]$info.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    [void]$process.Start()
    try {
        $output=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        Host-Command $process @{op='check';names=$names}
        $process.StandardInput.Close()
        if(!$process.WaitForExit(10000)) {throw 'The generated control-pipe child timed out.'}
        if($process.ExitCode -ne 0 -or $stderr.GetAwaiter().GetResult()) {throw 'The generated control-pipe child failed.'}
        return @($output.GetAwaiter().GetResult().Trim() -split '\r?\n')
    } finally {if(!$process.HasExited) {$process.Kill()}; $process.Dispose()}
}
$expected=@($names|ForEach-Object {([int[]][char[]]$_) -join ','})
$original=Join-Path $repo 'tools/test-native-batches.ps1'
$fixed=Join-Path $repo 'tools/test-native-batches-unicode.ps1'
$baseline=Observe-Names $original
$actual=Observe-Names $fixed
if(($baseline -join '|') -ceq ($expected -join '|')) {throw 'The original code-page loss was not reproduced.'}
if(($actual -join '|') -cne ($expected -join '|')) {throw 'The Unicode-safe control pipe changed a filename.'}
[pscustomobject]@{
    Stage='NativeControlPipeVerified'
    BaselineLossReproduced=$true
    ExactNames=$names.Count
    WriterCodePage=437
    BaselineCodePoints=$baseline
    VerifiedCodePoints=$actual
    OriginalRunnerSha256=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant()
    UnicodeRunnerSha256=(Get-FileHash -LiteralPath $fixed -Algorithm SHA256).Hash.ToLowerInvariant()
}
