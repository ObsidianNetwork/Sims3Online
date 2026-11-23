@echo off
rem SimsOnline tester kit: double-click wrapper for build-fork.ps1 (Windows PowerShell, this run only bypasses the script policy).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-fork.ps1" %*
pause
