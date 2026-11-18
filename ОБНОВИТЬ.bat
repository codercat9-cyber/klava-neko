@echo off
rem Update Klava. Run it from the NEW version folder (unpacked zip or git clone).
rem Finds your installed Klava (desktop shortcut / running / next folder), keeps your data, memory, models and outfits,
rem pulls new dependencies, rebuilds and starts her.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\update.ps1" %*
pause
