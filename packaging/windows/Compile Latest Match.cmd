@echo off
setlocal

set "APP=%~dp0Fortnite Match Compiler.exe"
if not exist "%APP%" (
  echo Fortnite Match Compiler.exe was not found beside this launcher.
  echo Extract the complete release ZIP, then try again.
  pause
  exit /b 1
)

start "" "%APP%" --compile-latest
exit /b 0
