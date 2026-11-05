@echo off
rem Double-click to remove MultiClaude (your Claude copies and their chats are kept).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
pause
