@echo off
rem SimsOnline tester kit: double-click wrapper for collect-logs.ps1 (Windows PowerShell, this run only bypasses the script policy).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-logs.ps1" %*
pause