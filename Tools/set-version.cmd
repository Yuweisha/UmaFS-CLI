@echo off
setlocal EnableDelayedExpansion
chcp 936 >nul

set "BASE=https://umapi.buenavista.fans:8443/umaver"
set "TOKFILE=%~dp0version_token.txt"
set "TOKEN="

if not exist "%TOKFILE%" (
    echo [ERROR] token file not found: %TOKFILE%
    echo Copy C:\Kitaworks\umaver\version_token.txt next to this script.
    pause
    exit /b 1
)

set /p TOKEN=<"%TOKFILE%"

if "%~1"=="" goto menu
if /i "%~1"=="show" goto show
if /i "%~1"=="set" goto setver
goto usage

:menu
echo ==============================
echo   Uma version manager
echo ==============================
echo Current:
call :showvalue
echo.
echo   1. Set new resource_version
echo   2. Show current value
echo   3. Exit
echo.
set /p CH=Choose [1-3]:
if "%CH%"=="1" goto askver
if "%CH%"=="2" goto show
exit /b 0

:askver
set /p NEWVER=New resource_version (e.g. 10021450:TZH99sxhsgYr):
goto apply

:setver
if "%~2"=="" (
    set /p NEWVER=New resource_version:
) else (
    set "NEWVER=%~2"
)
goto apply

:apply
if "%NEWVER%"=="" (
    echo Cancelled.
    pause
    exit /b 1
)
echo.
echo Submitting: %NEWVER%
curl -s -m 20 -X POST "%BASE%/version" -H "Authorization: Bearer %TOKEN%" -H "Content-Type: application/json" -d "{\"resource_version\":\"%NEWVER%\"}"
echo.
echo.
call :showvalue
pause
exit /b 0

:show
echo.
echo --- current resource_version ---
call :showvalue
echo.
echo --- full json ---
curl -s -m 20 "%BASE%/version"
echo.
pause
exit /b 0

:showvalue
curl -s -m 20 "%BASE%/version/value"
echo.
exit /b 0

:usage
echo Usage:
echo   set-version.cmd                     interactive
echo   set-version.cmd show                show current
echo   set-version.cmd set 10021450:xxxx   set directly
exit /b 1
