# Builds Aim Tracker with the C# compiler that ships with Windows (no SDK needed).
#   powershell -ExecutionPolicy Bypass -File build.ps1              -> dist\AimTracker\AimTracker.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Installer   -> also dist\AimTracker-Setup.exe + .sha256 (what a release ships)
param([switch]$Installer)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$app  = Join-Path $dist 'AimTracker'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw 'csc.exe not found - this needs 64-bit Windows with .NET Framework 4.' }
New-Item -ItemType Directory $app -Force | Out-Null

# the small WebView2 wrapper libraries come from NuGet (the WebView2 runtime itself is part of Windows 11)
$tmp = Join-Path $env:TEMP 'aimtracker-wv2'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null
Write-Host 'Downloading the WebView2 wrapper libraries from NuGet...'
Invoke-WebRequest 'https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2' -OutFile "$tmp\wv2.zip" -UseBasicParsing
Expand-Archive "$tmp\wv2.zip" "$tmp\x"
Copy-Item "$tmp\x\lib\net462\Microsoft.Web.WebView2.Core.dll", "$tmp\x\lib\net462\Microsoft.Web.WebView2.WinForms.dll", "$tmp\x\runtimes\win-x64\native\WebView2Loader.dll" $app -Force
Copy-Item (Join-Path $root 'src\index.html') $app -Force

& $csc /nologo /target:winexe "/out:$app\AimTracker.exe" /platform:x64 /optimize+ `
  "/r:$app\Microsoft.Web.WebView2.Core.dll" "/r:$app\Microsoft.Web.WebView2.WinForms.dll" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $root 'src\Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'compile failed' }
Write-Host "Built $app\AimTracker.exe"

if ($Installer) {
  # a single self-extracting setup made with Windows' built-in IExpress
  $stage = Join-Path $dist 'installer-stage'
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  New-Item -ItemType Directory $stage | Out-Null
  Copy-Item "$app\*" $stage
  Copy-Item (Join-Path $root 'installer\install.ps1') $stage
  $files = 'AimTracker.exe','index.html','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll','install.ps1'
  $target = Join-Path $dist 'AimTracker-Setup.exe'
  if (Test-Path $target) { Remove-Item $target -Force }
  $list = (0..($files.Count-1) | ForEach-Object { "%FILE$_%=" }) -join "`r`n"
  $strs = (0..($files.Count-1) | ForEach-Object { "FILE$_=`"$($files[$_])`"" }) -join "`r`n"
  $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$target
FriendlyName=Aim Tracker Setup
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$stage\
[SourceFiles0]
$list
[Strings]
$strs
"@
  # IExpress is picky: give it a short path without spaces or quotes
  $sedPath = Join-Path $env:TEMP 'aimtracker-setup.sed'
  Set-Content $sedPath $sed -Encoding ASCII
  Start-Process iexpress.exe -ArgumentList '/N', '/Q', $sedPath -Wait
  for ($i = 0; $i -lt 30 -and -not (Test-Path $target); $i++) { Start-Sleep 1 }
  if (-not (Test-Path $target)) { throw 'IExpress did not produce the installer' }
  Start-Sleep 2
  $hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLower()
  Set-Content "$target.sha256" "$hash  AimTracker-Setup.exe" -Encoding ASCII
  Write-Host ("Built {0} ({1:N0} KB)`nSHA-256 {2}" -f $target, ((Get-Item $target).Length/1KB), $hash)
}
