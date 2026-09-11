@echo off
rem DBPilot publish (Release) -> artifacts\publish (framework-dependent, needs .NET 10 runtime)
rem Output folder: frontend assets + backend + appsettings.json + run.bat (fixed port 5200)
setlocal
cd /d "%~dp0..\.."

set OUT=artifacts\publish

echo ============================================
echo  DBPilot publish -^> %OUT%
echo ============================================

echo [1/2] Building frontend ...
pushd web
if not exist node_modules call npm install
if errorlevel 1 goto :fail_popd
call npm run build
if errorlevel 1 goto :fail_popd
popd

echo [2/2] dotnet publish Release ...
dotnet publish samples\DBPilot.Sample.SqlServer -c Release -o %OUT%
if errorlevel 1 goto :fail

rem 脱敏：发布产物 appsettings.json 替换为模板（防凭据外发），使用前需填 DBPilot:ConnectionString
copy /y samples\DBPilot.Sample.SqlServer\appsettings.template.json %OUT%\appsettings.json >nul
if errorlevel 1 goto :fail

rem Published exe does not read launchSettings (defaults to Kestrel 5000) -> generate startup script fixed to 5200
(
    echo @echo off
    echo rem DBPilot startup - published artifact, port 5200, needs .NET 10 runtime
    echo setlocal
    echo set ASPNETCORE_URLS=http://0.0.0.0:5200
    echo DBPilot.Sample.SqlServer.exe
    echo pause
) > %OUT%\run.bat

echo.
echo [OK] Published to %OUT%
echo      Config: appsettings.json is the SANITIZED template -^> fill DBPilot:ConnectionString before use.
echo      Start: %OUT%\run.bat   -^>  http://localhost:5200
echo      Deploy: copy the whole %OUT% folder to the target machine.
goto :end

:fail_popd
popd 2>nul
:fail
echo.
echo [FAILED] Publish error, see log above.
pause
exit /b 1

:end
pause
