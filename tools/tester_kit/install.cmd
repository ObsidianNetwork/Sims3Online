@echo off
rem SimsOnline tester kit: double-click wrapper for install.ps1 (Windows PowerShell, this run only bypasses the script policy).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause