@echo off
setlocal
cd /d "%~dp0"
set "fx=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%fx%\csc.exe" set "fx=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
set "importer="
for %%V in (4.8.1 4.8) do if not defined importer if exist "%ProgramFiles(x86)%\Microsoft SDKs\Windows\v10.0A\bin\NETFX %%V Tools\TlbImp.exe" set "importer=%ProgramFiles(x86)%\Microsoft SDKs\Windows\v10.0A\bin\NETFX %%V Tools\TlbImp.exe"
if not defined importer for %%T in (tlbimp.exe) do set "importer=%%~$PATH:T"
if not defined importer (
    echo Building requires TlbImp.exe from the .NET Framework SDK.
    exit /b 1
)
set "interop=%TEMP%\TabTidy-build-%RANDOM%-%RANDOM%"
mkdir "%interop%" || exit /b 1
"%importer%" "%WINDIR%\System32\UIAutomationCore.dll" /out:"%interop%\UiaInterop.dll" /namespace:NativeUia /silent
if errorlevel 1 exit /b 1
"%fx%\csc.exe" /nologo /target:exe /platform:anycpu /out:TabTidy.exe /link:"%interop%\UiaInterop.dll" /reference:"%fx%\WPF\UIAutomationClient.dll" /reference:"%fx%\WPF\UIAutomationTypes.dll" /reference:"%fx%\WPF\WindowsBase.dll" TabTidy.cs
set "result=%errorlevel%"
del "%interop%\UiaInterop.dll"
rmdir "%interop%"
exit /b %result%
