@echo off
REM Taco Factory build: banned-terms check, art check, build, tests. Any failure stops the build.
setlocal
cd /d "%~dp0"
python tools\banned_terms.py || (echo Build stopped: banned terms found. & exit /b 1)
python -c "import PIL" 2>nul && (python tools\art\validate_art.py || (echo Build stopped: art check failed. & exit /b 1)) || echo Skipping art check: run "pip install pillow" to enable it.
dotnet build TacoFactory.sln -c Release || exit /b 1
dotnet test TacoFactory.sln -c Release --no-build || exit /b 1
echo.
echo Build OK.
echo   run_local.bat    server + game on this PC
echo   run_server.bat   dedicated server only
echo   run_client.bat   game only (add --host your.server.ip)
echo   run_bots.bat     6 bots play on your server (start it with --open)
echo   release.bat 0.3.0   build a release (docs\RELEASE.md)
