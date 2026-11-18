@echo off
rem ASCII-only wrapper: cmd.exe mis-parses UTF-8 batch files. Logic lives in tools\setup.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1"
pause
