@echo off
rem ASCII-only wrapper. Drag a .blend or .vrm onto this file to try it on Klava; no argument = Unskined.blend
where pwsh >nul 2>nul
if %errorlevel%==0 (pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0_scripts\swap.ps1" "%~1") else (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_scripts\swap.ps1" "%~1")
