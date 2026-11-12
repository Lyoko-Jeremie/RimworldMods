@echo off
chcp 65001 >nul
setlocal
rem 构建顺序很重要：兼容项目引用宿主 DLL，必须先编宿主。
rem 用法：
rem   build.bat              默认 Release，RimWorld 在默认位置
rem   build.bat Debug        指定配置
rem   build.bat Release "D:\RimWorld\"   指定 RimWorld 根目录

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release

set RIMWORLD=%~2
set RIMWORLD_ARG=
if not "%RIMWORLD%"=="" set RIMWORLD_ARG=-p:RimWorldDir=%RIMWORLD%

echo [1/5] 宿主
dotnet build "Source\JeremieAtmosphereOverhaulPatch\JeremieAtmosphereOverhaulPatch.csproj" -c %CONFIG% %RIMWORLD_ARG%
if errorlevel 1 goto fail

for %%D in (Compat.GoodClimate Compat.VanillaTemperatureExpanded Compat.CentralizedClimateControl Compat.OmniCrafter) do (
    echo [*] %%D
    dotnet build "Source\%%D\%%D.csproj" -c %CONFIG% %RIMWORLD_ARG%
    if errorlevel 1 goto fail
)

echo.
echo 构建完成：Assemblies\ 与 1.7\Compat\*\Assemblies\ 已更新。
exit /b 0

:fail
echo.
echo 构建失败，请看上面的错误。
exit /b 1
