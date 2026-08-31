@echo off
rem Legacy host: sends fixed C/S/F/M telemetry to the ESP32 (compatible protocol)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo csc.exe not found & exit /b 1)

if not exist lib\LibreHardwareMonitorLib.dll (
    echo Downloading LibreHardwareMonitorLib (MPL-2.0)...
    powershell -NoProfile -Command "$u='https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/download/v0.9.6/LibreHardwareMonitor.zip'; New-Item -ItemType Directory -Force -Path lib | Out-Null; Invoke-WebRequest -Uri $u -OutFile lib\lhm.zip; Expand-Archive lib\lhm.zip lib -Force; Remove-Item lib\lhm.zip -Force"
)

"%CSC%" /nologo /target:exe /out:monitor.exe /r:System.dll /r:System.Management.dll /r:lib\LibreHardwareMonitorLib.dll monitor.cs
if exist monitor.exe (echo OK: monitor.exe) else (echo BUILD FAILED)
