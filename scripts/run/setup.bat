@echo off
rem DBPilot first-run setup after clone: frontend build + solution build + sample appsettings
rem usage: setup.bat [SqlServer|MySql|Sqlite|PostgreSql]  (default SqlServer)
setlocal
cd /d "%~dp0..\.."

set SAMPLE=%1
if "%SAMPLE%"=="" set SAMPLE=SqlServer
set SAMPLE_DIR=samples\DBPilot.Sample.%SAMPLE%

if not exist "%SAMPLE_DIR%\appsettings.template.json" (
    echo [FAILED] Unknown sample "%SAMPLE%". Choose one of: SqlServer / MySql / Sqlite / PostgreSql
    exit /b 1
)

echo [1/3] Frontend: npm install + build ...
pushd web
call npm install
if errorlevel 1 goto :fail_popd
call npm run build
if errorlevel 1 goto :fail_popd
popd

echo [2/3] Backend: dotnet build ...
dotnet build
if errorlevel 1 goto :fail

echo [3/3] Prepare %SAMPLE_DIR% ...
if not exist "%SAMPLE_DIR%\appsettings.json" (
    copy "%SAMPLE_DIR%\appsettings.template.json" "%SAMPLE_DIR%\appsettings.json" >nul
    echo        appsettings.json created from template.
) else (
    echo        appsettings.json already exists, keep it.
)

echo.
echo ============ SETUP DONE ============
echo Next:
echo   1. Edit %SAMPLE_DIR%\appsettings.json  (connection string + Auth:Secret)
echo   2. dotnet run --project %SAMPLE_DIR%
goto :eof

:fail_popd
popd 2>nul
:fail
echo.
echo [FAILED] See log above.
pause
exit /b 1
