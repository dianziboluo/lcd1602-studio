@echo off
rem Build LCD1602Studio.exe with the built-in .NET Framework compiler (no installs)
rem First run auto-downloads LibreHardwareMonitorLib (MPL-2.0) into lib\
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo csc.exe not found & exit /b 1)

if not exist lib\LibreHardwareMonitorLib.dll (
    echo Downloading LibreHardwareMonitorLib (MPL-2.0)...
    powershell -NoProfile -Command "$u='https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/download/v0.9.6/LibreHardwareMonitor.zip'; New-Item -ItemType Directory -Force -Path lib | Out-Null; Invoke-WebRequest -Uri $u -OutFile lib\lhm.zip; Expand-Archive lib\lhm.zip lib -Force; Remove-Item lib\lhm.zip -Force"
)

"%CSC%" /nologo /codepage:65001 /target:winexe /out:LCD1602Studio.exe ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /r:System.Management.dll ^
    /r:lib\LibreHardwareMonitorLib.dll ^
    fontdata.cs Lcd1602.cs HwInfo.cs DataEngine.cs DataPalette.cs SerialSync.cs GlyphForm.cs PresetForm.cs MainForm.cs

if exist LCD1602Studio.exe (echo OK: LCD1602Studio.exe) else (echo BUILD FAILED)
