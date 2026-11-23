@echo off
rem SimsOnline tester kit: double-click wrapper for uninstall.ps1 (Windows PowerShell, this run only bypasses the script policy).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
pause