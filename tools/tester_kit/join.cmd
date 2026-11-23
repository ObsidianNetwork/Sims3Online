@echo off
rem SimsOnline tester kit: double-click wrapper for join.ps1 (Windows PowerShell, this run only bypasses the script policy).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0join.ps1" %*
pause