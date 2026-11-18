@echo off
rem ASCII-only wrapper: cmd.exe mis-parses UTF-8 batch files. Logic lives in tools\build.ps1 (Klava.sln -> bin, editor_bin).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build.ps1" %*
pause
