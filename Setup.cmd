@echo off
:: Couch Companion - setup app (install / update / uninstall). The only entry point.
:: Builds "build\Couch Companion Setup.exe" from src\Gui with the C# compiler that ships with
:: Windows (.NET Framework 4.x) and starts it; Windows asks for administrator rights.
setlocal
set "ROOT=%~dp0"
set "OUT=%ROOT%build\Couch Companion Setup.exe"
set "NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%NET%\csc.exe" set "NET=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%NET%\csc.exe" (
    echo .NET Framework 4.x C# compiler not found.
    pause
    exit /b 1
)
if not exist "%ROOT%build" mkdir "%ROOT%build"
"%NET%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 ^
    /win32manifest:"%ROOT%src\Gui\app.manifest" ^
    /reference:"%NET%\WPF\PresentationFramework.dll" /reference:"%NET%\WPF\PresentationCore.dll" ^
    /reference:"%NET%\WPF\WindowsBase.dll" /reference:System.Xaml.dll ^
    /resource:"%ROOT%src\Gui\MainWindow.xaml",MainWindow.xaml ^
    /out:"%OUT%" "%ROOT%src\Gui\*.cs"
if errorlevel 1 (
    echo.
    echo Build failed. / Kompilacja nie powiodla sie.
    pause
    exit /b 1
)
start "" "%OUT%" --src "%ROOT%src"
