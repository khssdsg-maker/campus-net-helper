@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo  CampusNetHelper - Build Check
echo ============================================
echo.

set OUT=%~dp0dist\CampusNetHelper.exe
if not exist "%~dp0dist" mkdir "%~dp0dist"

set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo [ERROR] .NET Framework 4.x compiler not found.
  pause
  exit /b 1
)

set WPF=%FW%\WPF
if not exist "%WPF%\PresentationCore.dll" (
  echo [ERROR] WPF assemblies not found at:
  echo         %WPF%
  pause
  exit /b 1
)

echo Framework: %FW%
echo WPF      : %WPF%
echo Output   : %OUT%
echo.

if exist "%OUT%" (
  del /q "%OUT%" 2>nul
  if exist "%OUT%" (
    echo [ERROR] Cannot replace the output file - it is in use.
    echo.
    echo         The program is probably still running.
    echo         Right-click its tray icon at the bottom-right corner and
    echo         choose "Exit", then run this script again.
    echo.
    pause
    exit /b 1
  )
)

REM ---- response file ----
> build.rsp echo /nologo
>> build.rsp echo /target:winexe
>> build.rsp echo /platform:anycpu
>> build.rsp echo /out:"%OUT%"
>> build.rsp echo /win32icon:"%~dp0assets\logo.ico"
>> build.rsp echo /lib:"%FW%"
>> build.rsp echo /lib:"%WPF%"
>> build.rsp echo /reference:System.dll
>> build.rsp echo /reference:System.Core.dll
>> build.rsp echo /reference:System.Windows.Forms.dll
>> build.rsp echo /reference:System.Drawing.dll
>> build.rsp echo /reference:System.Management.dll
>> build.rsp echo /reference:System.IO.Compression.dll
>> build.rsp echo /reference:System.IO.Compression.FileSystem.dll
>> build.rsp echo /reference:System.Xaml.dll
>> build.rsp echo /reference:PresentationCore.dll
>> build.rsp echo /reference:PresentationFramework.dll
>> build.rsp echo /reference:WindowsBase.dll
>> build.rsp echo /reference:UIAutomationProvider.dll
>> build.rsp echo /reference:UIAutomationTypes.dll
>> build.rsp echo /reference:WindowsFormsIntegration.dll

REM ---- local site config: create from the sample when missing ----
REM      SiteConfig.cs holds school-specific defaults and is NOT committed.
if not exist "%~dp0SiteConfig.cs" (
  if exist "%~dp0SiteConfig.sample.cs" (
    copy /y "%~dp0SiteConfig.sample.cs" "%~dp0SiteConfig.cs" >nul
    echo [i] SiteConfig.cs not found - created from SiteConfig.sample.cs
    echo     Edit it to set your own school's addresses.
    echo.
  )
)

set MISSINGSRC=
for %%F in (SiteConfig.cs App.xaml.cs Logger.cs HealthCheckStep.cs AutostartHelper.cs ConfigStore.cs DialEngine.cs QualityMonitor.cs KeepAlive.cs SpeedTestEngine.cs SpeedChart.cs PowGate.cs HealthReport.cs Theme.cs MainWindow.cs MainWindow.Logic.cs HealthWindow.cs SettingsWindow.cs SpeedTestWindow.cs WebAuthWindow.cs AccountWindow.cs) do (
  if exist "%%F" (
    >> build.rsp echo "%%F"
  ) else (
    echo [ERROR] missing source file: %%F
    set MISSINGSRC=1
  )
)
if defined MISSINGSRC (
  echo.
  echo [FAILED] some source files are missing.
  pause
  exit /b 1
)

"%FW%\csc.exe" @build.rsp

if errorlevel 1 (
  echo.
  echo [FAILED] compile errors above.
) else (
  echo.
  echo [OK] BUILD SUCCESS
  echo Output: %OUT%
  echo.
  echo Double-click the output file above to preview the UI.
)

echo.
pause
endlocal
