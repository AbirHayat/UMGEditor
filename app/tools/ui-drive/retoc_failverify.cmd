@echo off
rem test double for the 6g UI run: the real retoc for everything except verify, which fails
if /I "%~1"=="verify" (echo simulated verify failure 1>&2 & exit /b 3)
"E:\n3tron\patch\Tools\retoc-x86_64-pc-windows-msvc\retoc.exe" %*
exit /b %errorlevel%
