@echo off
rem ASCII-only wrapper: logic lives in rebuild_blend.ps1 (UTF-8 with BOM)
where pwsh >nul 2>nul
if %errorlevel%==0 (pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0rebuild_blend.ps1") else (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0rebuild_blend.ps1")
