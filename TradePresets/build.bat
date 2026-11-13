@echo off
chcp 65001 >nul
rem 按需修改为你的游戏安装目录
set "GAME_DIR=C:\Program Files (x86)\Steam\steamapps\common\Stronghold Crusader Definitive Edition"

tasklist /FI "IMAGENAME eq Stronghold Crusader Definitive Edition.exe" | find /I "Stronghold" >nul
if not errorlevel 1 (
  echo 请先关闭游戏再编译安装。
  pause
  exit /b 1
)

rem 优先使用用户目录下的 .NET SDK（系统 dotnet 可能只有运行时）
set "DOTNET=dotnet"
if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"

"%DOTNET%" build "%~dp0TradePresets.csproj" -c Release -p:GameDir="%GAME_DIR%"
pause
