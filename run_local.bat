@echo off
REM Starts a server on this PC (anyone may sign up) in its own window, then the game at the sign-in screen.
REM Sign in to 127.0.0.1; the first account you make is the server's administrator.
cd /d "%~dp0"
start "Templar Knights server" dotnet run --project src\TacoFactory.Server -c Release -- --open %*
timeout /t 5 /nobreak >nul
dotnet run --project src\TacoFactory.Client -c Release -- --host 127.0.0.1
