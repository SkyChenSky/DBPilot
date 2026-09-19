@echo off
rem DBPilot startup (single process: backend + bundled frontend)
setlocal
cd /d "%~dp0..\.."

echo ============================================
echo  DBPilot - http://localhost:5200
echo ============================================

if not exist "src\DBPilot.AspNetCore\wwwroot\index.html" (
    echo [1/2] Frontend build output missing, building...
    pushd web
    call npm install
    if errorlevel 1 goto :fail_popd
    call npm run build
    if errorlevel 1 goto :fail_popd
    popd
) else (
    echo [1/2] Frontend build output found, skip build
)

echo [2/2] Starting backend at http://localhost:5200 ...
dotnet run --project samples\DBPilot.Sample.SqlServer
goto :end

:fail_popd
popd 2>nul
echo.
echo [FAILED] Build error, see log above.
pause
exit /b 1

:end
pause
