[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable,[string[]]$PreviewFixtures,[string]$HtmlFixture,[string]$PlaybackFixtures,[string]$DraftModel)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'The executable has not been built.' }
$testRoot = Join-Path $PSScriptRoot ('../.build/smoke-' + [guid]::NewGuid().ToString('N'))
$testRoot = [IO.Path]::GetFullPath($testRoot)
$app = New-Item -ItemType Directory -Path (Join-Path $testRoot 'app') -Force
$temp = New-Item -ItemType Directory -Path (Join-Path $testRoot 'temp') -Force
$scratch = New-Item -ItemType Directory -Path (Join-Path $testRoot 'data') -Force
$copy = Join-Path $app.FullName 'Reviewer Smoke Test.exe'
Copy-Item -LiteralPath $Executable -Destination $copy
$modelCopy = Join-Path $app.FullName 'Marqo-NSFW-384.onnx'
if($DraftModel){Copy-Item -LiteralPath $DraftModel -Destination $modelCopy}
$watchers=@($app,$temp | ForEach-Object {
    $watcher=New-Object IO.FileSystemWatcher $_.FullName
    $watcher.IncludeSubdirectories=$true
    $watcher.NotifyFilter=[IO.NotifyFilters]::FileName
    $id='MediaReviewerSmoke-'+[guid]::NewGuid().ToString('N')
    Register-ObjectEvent -InputObject $watcher -EventName Created -SourceIdentifier $id | Out-Null
    $watcher.EnableRaisingEvents=$true
    [pscustomobject]@{Watcher=$watcher;Id=$id}
})
function Invoke-Check([string]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $copy
    $info.Arguments = $Arguments
    $info.UseShellExecute = $false
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['TEMP'] = $temp.FullName
    $info.EnvironmentVariables['TMP'] = $temp.FullName
    $info.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $temp.FullName
    $process = [Diagnostics.Process]::Start($info)
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(90000)) { $process.Kill(); throw "Timed out: $Arguments" }
        if ($process.ExitCode -ne 0) { throw "Exit $($process.ExitCode): $Arguments`n$($errors.Result)" }
    } finally { $process.Dispose() }
}
try {
    Invoke-Check '--verify-dependencies'
    if($DraftModel){Invoke-Check '--verify-draft-model'}
    $selfTest='--self-test "' + $scratch.FullName + '"'
    if($PlaybackFixtures){$selfTest+=' "' + (Resolve-Path -LiteralPath $PlaybackFixtures).Path + '"'}
    Invoke-Check $selfTest
    Invoke-Check ('--window-smoke "' + (Join-Path $scratch.FullName 'review.sqlite') + '"')
    Invoke-Check ('--first-scan-smoke "' + $scratch.FullName + '"')
    $cliDatabase = Join-Path $scratch.FullName 'command-line.sqlite'
    Invoke-Check ('--create-database "' + $cliDatabase + '"')
    Invoke-Check ('--database "' + $cliDatabase + '" --scan-root "' + (Join-Path $scratch.FullName 'home-share') + '" --extensions png --full-hash --workers 1')
    if($PreviewFixtures){Invoke-Check ('--verify-previews ' + (($PreviewFixtures | ForEach-Object {'"'+(Resolve-Path -LiteralPath $_).Path+'"'}) -join ' '))}
    if($HtmlFixture){
        $original=(Resolve-Path -LiteralPath $HtmlFixture).Path
        $hash=(Get-FileHash -LiteralPath $original).Hash
        Invoke-Check ('--verify-import "' + $original + '" "' + (Join-Path $scratch.FullName 'imported.sqlite') + '"')
        if((Get-FileHash -LiteralPath $original).Hash -ne $hash){throw 'HTML import modified the original fixture.'}
    }
    $allowed = @($copy)
    if($DraftModel){$allowed += $modelCopy}
    $unexpected = @(Get-ChildItem -LiteralPath $app.FullName,$temp.FullName -File -Force -Recurse | Where-Object { $_.FullName -notin $allowed })
    if ($unexpected.Count) { throw ('Unexpected files beside the executable or under TEMP: ' + ($unexpected.FullName -join ', ')) }
    foreach($registration in $watchers) {
        $extracted=@(Get-Event -SourceIdentifier $registration.Id -ErrorAction SilentlyContinue | ForEach-Object {$_.SourceEventArgs.FullPath} | Where-Object {$_ -match '\.(dll|exe)$'})
        if($extracted.Count){throw ('Unexpected native file creation: '+($extracted -join ', '))}
    }
    Write-Output "Single-file smoke passed: $testRoot"
} finally {
    foreach($registration in $watchers){Unregister-Event -SourceIdentifier $registration.Id;Get-Event -SourceIdentifier $registration.Id -ErrorAction SilentlyContinue | Remove-Event;$registration.Watcher.Dispose()}
}
