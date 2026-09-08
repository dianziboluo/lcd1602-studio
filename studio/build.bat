@echo off
rem Build LCD1602Studio.exe with the built-in .NET Framework compiler (no installs)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo csc.exe not found & exit /b 1)

if not exist lib\LibreHardwareMonitorLib.dll (
    echo copying LHM lib...
    xcopy /E /I /Y /Q "..\pc_monitor\lib" lib >nul
)

"%CSC%" /nologo /codepage:65001 /target:winexe /out:LCD1602Studio.exe ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /r:System.Management.dll /r:System.Web.Extensions.dll ^
    /r:lib\LibreHardwareMonitorLib.dll ^
    UiTheme.cs fontdata.cs Lcd1602.cs HwInfo.cs DataEngine.cs DataPalette.cs SerialSync.cs GlyphForm.cs PresetForm.cs MainForm.cs ^
    Modules\IModule.cs Modules\ModuleHost.cs Modules\TcpInfo.cs Modules\DshModule.cs Modules\DsModule.cs

if exist LCD1602Studio.exe (echo OK: LCD1602Studio.exe) else (echo BUILD FAILED)
