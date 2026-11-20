@echo off
REM Builds a release: release.bat 0.3.0  (see docs\RELEASE.md)
cd /d "%~dp0"
python tools\release\make_release.py %*
