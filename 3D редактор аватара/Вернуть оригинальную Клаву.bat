@echo off
rem ASCII-only wrapper: bring back the original Klava model after a try-on
where pwsh >nul 2>nul
if %errorlevel%==0 (pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0_scripts\restore.ps1") else (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_scripts\restore.ps1")
