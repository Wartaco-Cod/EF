@echo off
REM Templar Knights server. Common options:
REM   --open                    anyone may make an account (otherwise a purchase key is needed)
REM   --make-keys 10            print 10 new purchase keys and exit
REM   --name "My Server"        shown on the sign-in screen
REM   --updates <folder>        serve game updates to players' launchers (TCP 7778)
REM   --port 7777 --saves saves
REM Type "help" in the server window for moderation commands. See docs\HOSTING.md.
cd /d "%~dp0"
dotnet run --project src\TacoFactory.Server -c Release -- %*
