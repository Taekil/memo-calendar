@echo off
rem Build with the .NET Framework 4 compiler included in Windows 10/11.
cd /d %~dp0
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:MemoCalendar.exe /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll src\MemoCalendar.cs
if errorlevel 1 (echo Build failed) else (echo Built: MemoCalendar.exe)
