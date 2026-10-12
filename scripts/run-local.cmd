@echo off
rem A Practice session against the AI on Windows (scripts\windows_play.py run).
rem   scripts\run-local.cmd [--windowed] [--username NAME] [--workers N] [--stock-ai] [--smoke]
setlocal
set "VENV_PY=%~dp0..\.venv\Scripts\python.exe"
if not exist "%VENV_PY%" (
  echo run-local: no .venv; run scripts\setup.cmd first 1>&2
  exit /b 1
)
"%VENV_PY%" "%~dp0windows_play.py" run %*
