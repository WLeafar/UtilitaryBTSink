@echo off
chcp 65001 >nul
title UtilitaryBTSink Installer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
echo.
pause
