@echo off
rem IS4420WIN Current Loop Receiver - build with the C# compiler included with Windows
rem (.NET Framework 4.x, C# 5). No Visual Studio needed.
rem Builds IS4420WIN_<version>.exe (for example IS4420WIN_v0.03.exe), then builds and runs
rem the tests. The version comes from AppInfo.Version in src\Support.cs.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo The .NET Framework 4.x C# compiler was not found.
    exit /b 1
)

rem The text between the first pair of quotes on the line: public const string Version = "v0.03";
set "VER="
for /f tokens^=2^ delims^=^" %%v in ('findstr /c:"public const string Version" src\Support.cs') do set "VER=%%v"
if not defined VER (
    echo The version was not found in src\Support.cs.
    exit /b 1
)
set "EXE=IS4420WIN_%VER%.exe"

"%CSC%" /nologo /target:winexe /optimize+ /out:%EXE% /win32icon:IS4420WIN.ico /resource:IS4420WIN.ico /resource:inacks_logo.png /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\*.cs
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built %EXE%

"%CSC%" /nologo /out:tests\LineTests.exe tests\LineTests.cs src\Is4420Line.cs src\PortScanner.cs src\SerialLink.cs
if errorlevel 1 (
    echo Test build failed.
    exit /b 1
)
tests\LineTests.exe
