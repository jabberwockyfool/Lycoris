@echo off
rem Double-click launcher for the ykport GUI.
cd /d "%~dp0"
py ykport_gui.py
if errorlevel 1 (
  echo.
  echo ykport exited with an error.
  pause
)
