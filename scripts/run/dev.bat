@echo off
rem DBPilot debug mode: dotnet watch (backend) + vite (frontend) in two windows
setlocal
cd /d "%~dp0..\.."

echo ============================================
echo  DBPilot DEBUG MODE (hot reload)
echo   Frontend: http://localhost:5173  (/api -> 5200)
echo   Backend : http://localhost:5200  Swagger: /swagger
echo ============================================

if not exist "web\node_modules" (
    echo [prepare] Installing frontend dependencies...
    pushd web
    call npm install
    if errorlevel 1 goto :fail_popd
    popd
)

start "DBPilot-Backend (dotnet watch)" cmd /k "cd /d "%CD%" && dotnet watch --project samples\DBPilot.Sample.SqlServer"
timeout /t 3 /nobreak >nul
start "DBPilot-Frontend (vite)" cmd /k "cd /d "%CD%\web" && npm run dev"

echo.
echo Two windows started:
echo   - Backend (dotnet watch): rebuild+restart on C# changes
echo   - Frontend (vite): hot reload on Vue/TS changes
echo Open http://localhost:5173 in browser.
echo Close the windows to stop.
goto :eof

:fail_popd
popd 2>nul
echo [FAILED] npm install failed, check Node.js environment.
pause
exit /b 1
