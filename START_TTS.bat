@echo off
rem ASCII-only: start Klava's big voice server (Qwen3-TTS, port 8095) by hand for debugging.
rem KlavaDesk starts it automatically; this is only for looking at its log in a console.
cd /d "%~dp0tts"
"%~dp0tts\.venv\Scripts\python.exe" "%~dp0tts\server.py" --port 8095
pause
