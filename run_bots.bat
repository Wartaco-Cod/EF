@echo off
REM Playtest bots against a server started with --open (default: this PC). Add --host to target another machine.
REM Network checks: --duel-test, --economy-test, --auth-test <saves>, --story-test (see README for each server's options).
REM Balance without a server: --simulate all [group size]   or   --campaign [group size] [out.csv]
cd /d "%~dp0"
dotnet run --project src\TacoFactory.Bots -c Release -- --count 6 %*
