@echo off
rem DBPilot full check: dotnet build + dotnet test + npm run build
setlocal
cd /d "%~dp0..\.."

echo [1/3] Build solution...
dotnet build
if errorlevel 1 goto :fail

echo [2/3] Run unit tests...
dotnet test --no-build
if errorlevel 1 goto :fail

echo [3/3] Frontend type-check and build...
pushd web
call npm run build
if errorlevel 1 (popd & goto :fail)
popd

echo.
echo ============ ALL PASSED ============
pause
exit /b 0

:fail
echo.
echo ============ FAILED, see log above ============
pause
exit /b 1
