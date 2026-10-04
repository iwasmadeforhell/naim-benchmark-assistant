$ErrorActionPreference = 'Stop'
$name    = 'N.AIM Benchmark Assistant'
$exeName = 'NAIM-Benchmark-Assistant.exe'
$proc    = 'NAIM-Benchmark-Assistant'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$dst = Join-Path $env:LOCALAPPDATA "Programs\$name"

# stop the running app, and remove the old "Aim Tracker" install if there is one
Get-Process $proc, AimTracker -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
$old = Join-Path $env:LOCALAPPDATA 'Programs\AimTracker'
if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Aim Tracker.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Aim Tracker.lnk') -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AimTracker' -ErrorAction SilentlyContinue

New-Item -ItemType Directory $dst -Force | Out-Null
foreach ($f in $exeName, 'index.html', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll') {
  Copy-Item (Join-Path $src $f) $dst -Force
}
$exe = Join-Path $dst $exeName

# uninstaller (your tracked scenarios in %LOCALAPPDATA%\$name are kept)
@"
Get-Process $proc -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "`$env:USERPROFILE\Desktop\$name.lnk" -ErrorAction SilentlyContinue
Remove-Item "`$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$name.lnk" -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NAIMBenchmarkAssistant' -ErrorAction SilentlyContinue
Start-Process cmd -ArgumentList '/c timeout 2 >nul & rmdir /s /q "$dst"' -WindowStyle Hidden
"@ | Set-Content (Join-Path $dst 'uninstall.ps1') -Encoding UTF8

$sh = New-Object -ComObject WScript.Shell
foreach ($lnk in (Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk"),
                 (Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk")) {
  $s = $sh.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = $dst; $s.IconLocation = "$exe,0"; $s.Save()
}

$ver = (Get-Item $exe).VersionInfo.ProductVersion
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NAIMBenchmarkAssistant'
New-Item $k -Force | Out-Null
Set-ItemProperty $k DisplayName $name
Set-ItemProperty $k DisplayVersion $ver
Set-ItemProperty $k Publisher 'N.AIM'
Set-ItemProperty $k InstallLocation $dst
Set-ItemProperty $k DisplayIcon $exe
Set-ItemProperty $k UninstallString ("powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File `"" + (Join-Path $dst 'uninstall.ps1') + "`"")
Start-Process $exe
