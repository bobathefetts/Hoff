[CmdletBinding()]
param([switch]$ManagedOnly,[string]$OutputName='Media Compliance Reviewer.exe',[string]$Distribution='Ubuntu-24.04')
$ErrorActionPreference='Stop'
if($OutputName -notmatch '^[^\\/:*?"<>|]+\.exe$'){throw 'OutputName must be an executable filename.'}
if($Distribution -notmatch '^[A-Za-z0-9._-]+$'){throw 'Distribution must be the name of a WSL distribution.'}
$root=$PSScriptRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$work=New-Item -ItemType Directory -Force -Path "$root/.build"
$null=New-Item -ItemType Directory -Force -Path "$root/dist"
$references=@('System','System.Core','System.Xml','System.Xaml','System.Windows.Forms','System.IO.Compression','System.Runtime.Serialization','System.Web.Extensions' | ForEach-Object { "/r:$framework/$_.dll" })
$references+=@('WindowsBase','PresentationCore','PresentationFramework' | ForEach-Object { "/r:$framework/WPF/$_.dll" })
$sources=@(Get-ChildItem -LiteralPath "$root/Core","$root/App" -Filter *.cs -File | ForEach-Object FullName)
# Symbols only for the managed-only development build: /debug also writes the absolute PDB path into the
# image, and the native host embeds that image unchanged in the release executable.
$debugSwitch=@();if($ManagedOnly){$debugSwitch=@('/debug:pdbonly')}
& "$framework/csc.exe" /nologo /target:exe /platform:x64 /optimize+ @debugSwitch /codepage:65001 "/out:$work/Application.exe" "/resource:$root/App/MainWindow.xaml,MainWindow.xaml" "/resource:$root/native/reviewer.ico,Reviewer.ico" "/resource:$root/native/COPYING.LGPLv2.1,COPYING.LGPLv2.1" "/resource:$root/native/COPYING.dav1d,COPYING.dav1d" "/resource:$root/native/COPYING.draft-ai,COPYING.draft-ai" "/resource:$root/native/COPYING.gemma-runtime,COPYING.gemma-runtime" @references @sources
if($LASTEXITCODE -ne 0){throw 'C# compilation failed.'}
if($ManagedOnly){return}
# --exec skips the Linux shell, which would strip the backslashes from the Windows path.
$linuxPath = & wsl.exe -d $Distribution --exec wslpath -a -u "$root"
if($LASTEXITCODE -ne 0){throw "Cannot locate the Windows project inside the WSL distribution '$Distribution'."}
& wsl.exe -d $Distribution -- bash "$linuxPath/native/build.sh"
if($LASTEXITCODE -ne 0){throw 'Native linking failed.'}
Move-Item -LiteralPath "$work/Reviewer-linked.exe" -Destination "$root/dist/$OutputName" -Force
