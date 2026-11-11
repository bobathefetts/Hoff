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
# A DLL dropped beside the executable (often in Downloads) must never be loaded in place of the Windows one. This
# copies genuine System32 DLLs next to a second copy of the exe, runs the window-and-scan test from there, and
# watches every module the process loads.
function Test-PlantedDlls {
    $planted = New-Item -ItemType Directory -Path (Join-Path $testRoot 'planted') -Force
    $plantedData = New-Item -ItemType Directory -Path (Join-Path $planted.FullName 'data') -Force
    $plantedExe = Join-Path $planted.FullName 'Reviewer Smoke Test.exe'
    Copy-Item -LiteralPath $Executable -Destination $plantedExe
    foreach($name in 'bcrypt.dll','uxtheme.dll','version.dll','dwmapi.dll','d3d9.dll','winmm.dll','propsys.dll','mfplat.dll','windowscodecs.dll') {
        $source = Join-Path $env:WINDIR "System32\$name"
        if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $planted.FullName}
    }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $plantedExe
    $info.Arguments = '--first-scan-smoke "' + $plantedData.FullName + '"'
    $info.UseShellExecute = $false
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['TEMP'] = $temp.FullName
    $info.EnvironmentVariables['TMP'] = $temp.FullName
    $loaded = @{}
    $process = [Diagnostics.Process]::Start($info)
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while(!$process.HasExited) {
            if($watch.Elapsed.TotalSeconds -gt 90){$process.Kill();throw 'Timed out: planted-DLL check'}
            try{$process.Refresh();foreach($module in $process.Modules){$loaded[$module.FileName]=$true}}catch{}
            Start-Sleep -Milliseconds 150
        }
        if($process.ExitCode -ne 0){throw "Exit $($process.ExitCode): planted-DLL check`n$($errors.Result)"}
    } finally { $process.Dispose() }
    $bad = @($loaded.Keys | Where-Object { $_.StartsWith($planted.FullName,[StringComparison]::OrdinalIgnoreCase) -and $_ -ne $plantedExe })
    if($bad.Count){throw ('A DLL planted beside the executable was loaded: ' + ($bad -join ', '))}
    if(!@($loaded.Keys | Where-Object { $_ -like '*\System32\bcrypt.dll' }).Count){throw 'The planted-DLL check never saw bcrypt.dll load from System32.'}
}
# User-facing messages must name this app, not the upstream project or a decoder exe that does not exist.
$staleNames = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../App'),(Join-Path $PSScriptRoot '../Core') -Filter *.cs -File |
    Select-String -SimpleMatch -Pattern 'FleetHacker.exe','beside FleetHacker','Run FleetHacker','MediaReviewer.Decoder.exe','approved endpoint logs')
if($staleNames.Count){throw ('Messages name the wrong program: ' + (($staleNames | ForEach-Object {"$($_.Filename):$($_.LineNumber)"}) -join ', '))}
try {
    Invoke-Check '--verify-dependencies'
    if($DraftModel){Invoke-Check '--verify-draft-model'}
    $selfTest='--self-test "' + $scratch.FullName + '"'
    if($PlaybackFixtures){$selfTest+=' "' + (Resolve-Path -LiteralPath $PlaybackFixtures).Path + '"'}
    Invoke-Check $selfTest
    Invoke-Check ('--window-smoke "' + (Join-Path $scratch.FullName 'review.sqlite') + '"')
    Invoke-Check ('--first-scan-smoke "' + $scratch.FullName + '"')
    Test-PlantedDlls
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
