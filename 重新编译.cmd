@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "COMPILER=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%COMPILER%" set "COMPILER=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%COMPILER%" /nologo /target:winexe /optimize+ /utf8output /win32manifest:app.manifest /out:上班鼓励器.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll 上班鼓励器.cs
if errorlevel 1 (echo 编译失败。 & pause & exit /b 1)
echo 编译完成，双击 上班鼓励器.exe 运行。
pause
