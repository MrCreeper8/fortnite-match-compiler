@echo off
setlocal

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
  echo Installation finished successfully.
) else (
  echo Installation failed. Review the message above, then try again.
)
pause
exit /b %RESULT%
