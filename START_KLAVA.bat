@echo off
rem ASCII-only wrapper: cmd.exe mis-parses UTF-8 batch files. Logic lives in START_KLAVA.ps1.
where pwsh >nul 2>&1
if %errorlevel%==0 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0START_KLAVA.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0START_KLAVA.ps1"
)
