@echo off

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publicar-release.ps1"
exit /b %errorlevel%
