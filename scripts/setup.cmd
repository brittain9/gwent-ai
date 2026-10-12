@echo off
rem One-time setup for Practice against the AI on Windows (scripts\windows_play.py).
rem   scripts\setup.cmd [--client DIR] [--trust-cert, --untrust-cert or --uninstall]
setlocal
set "VENV_PY=%~dp0..\.venv\Scripts\python.exe"
if exist "%VENV_PY%" (
  "%VENV_PY%" "%~dp0windows_play.py" setup %*
  exit /b
)
py -3 -c "" >nul 2>nul
if not errorlevel 1 (
  py -3 "%~dp0windows_play.py" setup %*
  exit /b
)
python "%~dp0windows_play.py" setup %*
