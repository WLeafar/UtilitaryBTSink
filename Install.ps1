$ErrorActionPreference = "Stop"

Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "  UtilitaryBTSink Installer (v1.0)" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host ""

$root = $PSScriptRoot
$csproj = Join-Path $root "UtilitaryBTSink\UtilitaryBTSink.csproj"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\UtilitaryBTSink"
$exePath = Join-Path $installDir "UtilitaryBTSink.exe"
$iconPath = Join-Path $installDir "Assets\app.ico"

if (-not (Test-Path $csproj)) {
    Write-Host "[ERROR] Project file not found at: $csproj" -ForegroundColor Red
    exit 1
}

Write-Host "[1/5] Stopping any running instance..." -ForegroundColor Yellow
Get-Process -Name "UtilitaryBTSink" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Write-Host "[2/5] Locating Visual Studio 2022 MSBuild..." -ForegroundColor Yellow
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Host "[ERROR] Visual Studio 2022 not found. Please install Visual Studio 2022 Community with the Windows App SDK workload." -ForegroundColor Red
    exit 1
}

$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) {
    Write-Host "[ERROR] MSBuild.exe was not found in your Visual Studio installation." -ForegroundColor Red
    exit 1
}

Write-Host "[3/5] Building standalone Release | x64 binary..." -ForegroundColor Yellow
dotnet restore $csproj -r win-x64 -p:SelfContained=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] dotnet restore failed (exit code $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

& $msbuild $csproj /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /nologo /v:minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] MSBuild compilation failed (exit code $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

$buildOut = Join-Path $root "UtilitaryBTSink\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
if (-not (Test-Path (Join-Path $buildOut "UtilitaryBTSink.exe"))) {
    Write-Host "[ERROR] Compiled executable not found at: $buildOut" -ForegroundColor Red
    exit 1
}

Write-Host "[4/5] Installing files to $installDir..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $buildOut "*") -Destination $installDir -Recurse -Force

# Generate native uninstaller script inside the installation folder
$uninstallBat = Join-Path $installDir "Uninstall.bat"
$uninstallScript = @"
@echo off
chcp 65001 >nul
echo Closing UtilitaryBTSink...
taskkill /F /IM UtilitaryBTSink.exe >nul 2>&1
timeout /t 1 /nobreak >nul
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "UtilitaryBTSink" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\UtilitaryBTSink" /f >nul 2>&1
del /F /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\UtilitaryBTSink.lnk" >nul 2>&1
del /F /Q "%USERPROFILE%\Desktop\UtilitaryBTSink.lnk" >nul 2>&1
echo Removing installed files...
start "" cmd /c "timeout /t 2 /nobreak >nul && rmdir /S /Q "$installDir""
echo UtilitaryBTSink has been uninstalled.
"@
Set-Content -Path $uninstallBat -Value $uninstallScript -Encoding ASCII

Write-Host "[5/5] Creating shortcuts and enabling Windows startup..." -ForegroundColor Yellow
$wsh = New-Object -ComObject WScript.Shell

$desktopDir = [Environment]::GetFolderPath("Desktop")
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"

foreach ($lnkPath in @(
    (Join-Path $desktopDir "UtilitaryBTSink.lnk"),
    (Join-Path $startMenuDir "UtilitaryBTSink.lnk")
)) {
    $sc = $wsh.CreateShortcut($lnkPath)
    $sc.TargetPath = $exePath
    $sc.WorkingDirectory = $installDir
    if (Test-Path $iconPath) { $sc.IconLocation = "$iconPath,0" }
    $sc.Description = "Windows Bluetooth A2DP Sink Audio Receiver"
    $sc.Save()
}

# Register automatic minimized startup on Windows login (matches in-app Settings toggle)
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "UtilitaryBTSink" -Value "`"$exePath`" --minimized"

# Register in Windows Settings -> Apps (Uninstall)
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UtilitaryBTSink"
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value "UtilitaryBTSink"
Set-ItemProperty -Path $uninstallKey -Name "DisplayVersion" -Value "1.0.0"
Set-ItemProperty -Path $uninstallKey -Name "Publisher" -Value "UtilitaryBTSink"
Set-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value $installDir
Set-ItemProperty -Path $uninstallKey -Name "DisplayIcon" -Value $exePath
Set-ItemProperty -Path $uninstallKey -Name "UninstallString" -Value "`"$uninstallBat`""
Set-ItemProperty -Path $uninstallKey -Name "NoModify" -Type DWord -Value 1
Set-ItemProperty -Path $uninstallKey -Name "NoRepair" -Type DWord -Value 1

Write-Host ""
Write-Host "Installation completed successfully!" -ForegroundColor Green
Write-Host "Installed folder : $installDir" -ForegroundColor Gray
Write-Host "Shortcuts        : Desktop and Start Menu" -ForegroundColor Gray
Write-Host "Windows Startup  : Enabled (starts minimized to tray)" -ForegroundColor Gray
Write-Host ""
Write-Host "You can now delete this temporary extracted folder and close Visual Studio." -ForegroundColor Cyan

Start-Process -FilePath $exePath -WorkingDirectory $installDir
