@echo off
REM The game. It opens at the sign-in screen; --host fills in your server's address.
REM   run_client.bat --host 203.0.113.5
REM Quick play on an open server (skips sign-in; account and character take this name):
REM   run_client.bat --host 127.0.0.1 --name "Sir Amaury" --class Paladin
cd /d "%~dp0"
dotnet run --project src\TacoFactory.Client -c Release -- %*
