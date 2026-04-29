@echo off
echo ============================================================
echo  TrackPoint Blocker - Build Script
echo ============================================================

:: Try 64-bit .NET Framework 4.x compiler first
set CSC64=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set CSC32=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if exist "%CSC64%" (
    echo Using: %CSC64%
    "%CSC64%" /target:winexe /out:TrackPointBlocker.exe TrackPointBlocker.cs
    goto done
)

if exist "%CSC32%" (
    echo Using: %CSC32%
    "%CSC32%" /target:winexe /out:TrackPointBlocker.exe TrackPointBlocker.cs
    goto done
)

echo ERROR: Could not find csc.exe.
echo Make sure .NET Framework 4.x is installed.
pause
exit /b 1

:done
if %ERRORLEVEL%==0 (
    echo.
    echo  Build successful! Run TrackPointBlocker.exe to start.
) else (
    echo.
    echo  Build FAILED. Check the errors above.
)
pause
