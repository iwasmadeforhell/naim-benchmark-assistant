$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$dst = Join-Path $env:LOCALAPPDATA 'Programs\AimTracker'
Get-Process AimTracker -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
New-Item -ItemType Directory $dst -Force | Out-Null
foreach ($f in 'AimTracker.exe','index.html','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll') {
  Copy-Item (Join-Path $src $f) $dst -Force
}
$exe = Join-Path $dst 'AimTracker.exe'

# uninstaller (your tracked scenarios in %LOCALAPPDATA%\SensSwitcher are kept)
@"
Get-Process AimTracker -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "`$env:USERPROFILE\Desktop\Aim Tracker.lnk" -ErrorAction SilentlyContinue
Remove-Item "`$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Aim Tracker.lnk" -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AimTracker' -ErrorAction SilentlyContinue
Start-Process cmd -ArgumentList '/c timeout 2 >nul & rmdir /s /q "$dst"' -WindowStyle Hidden
"@ | Set-Content (Join-Path $dst 'uninstall.ps1') -Encoding UTF8

$sh = New-Object -ComObject WScript.Shell
foreach ($lnk in (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Aim Tracker.lnk'),
                 (Join-Path ([Environment]::GetFolderPath('Programs')) 'Aim Tracker.lnk')) {
  $s = $sh.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = $dst; $s.Save()
}

$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AimTracker'
New-Item $k -Force | Out-Null
Set-ItemProperty $k DisplayName 'Aim Tracker'
Set-ItemProperty $k DisplayVersion '1.0'
Set-ItemProperty $k InstallLocation $dst
Set-ItemProperty $k DisplayIcon $exe
Set-ItemProperty $k UninstallString ("powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File `"" + (Join-Path $dst 'uninstall.ps1') + "`"")
Start-Process $exe
