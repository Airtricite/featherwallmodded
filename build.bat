@echo off
setlocal

rem ================================================================
rem  FeatherWall build helper
rem
rem    build.bat            Release build (default)
rem    build.bat run        Release build, then launch the app
rem    build.bat publish    single-file framework-dependent publish
rem                         into publish\, zipped like the release
rem    build.bat clean      delete bin, obj and publish
rem
rem  Paths are relative and the script pushes its own folder first,
rem  so it can live anywhere and be run from anywhere.
rem ================================================================

pushd "%~dp0" || exit /b 1

set "PROJ=src\FeatherWall\FeatherWall.csproj"
set "EXE=src\FeatherWall\bin\Release\net10.0-windows10.0.19041.0\featherwall.exe"

rem A stale Godot config in the user's NuGet profile registers a fallback
rem package folder under a user that no longer exists, and NuGet fails the
rem restore with NU1301 because of it. Pointing the property at the real
rem global-packages folder overrides that entry. Harmless if the Godot
rem config is ever cleaned up, because the folder it names is one NuGet
rem already searches.
set "FALLBACK=%USERPROFILE%\.nuget\packages"

for /f "delims=" %%s in ('dotnet --list-sdks 2^>nul') do set "SDK=%%s"
if not defined SDK (
    echo [x] No .NET SDK found. Install the .NET 10 SDK first:
    echo     https://dotnet.microsoft.com/download
    popd
    exit /b 1
)

set "TASK=%~1"
if "%TASK%"=="" set "TASK=build"

set "RC=0"
if /i "%TASK%"=="build"   call :build   || set "RC=1"
if /i "%TASK%"=="run"     call :run     || set "RC=1"
if /i "%TASK%"=="publish" call :publish || set "RC=1"
if /i "%TASK%"=="clean"   call :clean   || set "RC=1"

if /i "%TASK%"=="build"   goto finish
if /i "%TASK%"=="run"     goto finish
if /i "%TASK%"=="publish" goto finish
if /i "%TASK%"=="clean"   goto finish

echo Usage: build.bat [build ^| run ^| publish ^| clean]
set "RC=1"

:finish
popd
exit /b %RC%


:build
echo == Building Release ==
dotnet build "%PROJ%" -c Release --nologo "/p:RestoreFallbackFolders=%FALLBACK%" || exit /b 1
echo.
echo Done: %EXE%
exit /b 0


:run
call :build || exit /b 1
echo.
echo == Starting FeatherWall ==
start "" "%EXE%"
exit /b 0


:publish
echo == Publishing single-file, framework-dependent ==
if exist publish rmdir /s /q publish
dotnet publish "%PROJ%" -c Release -o publish --nologo ^
    /p:PublishSingleFile=true /p:SelfContained=false /p:DebugType=embedded ^
    "/p:RestoreFallbackFolders=%FALLBACK%" || exit /b 1

echo.
rem The version comes from the published assembly, so it can never
rem drift from the csproj. ProductVersion may carry a +<commit>
rem suffix; everything after the + is dropped.
powershell -NoProfile -Command "$v=(Get-Item 'publish\featherwall.exe').VersionInfo.ProductVersion.Split('+')[0]; $n='featherwall-v'+$v+'-win-x64.zip'; Compress-Archive -Path 'publish\*' -DestinationPath $n -Force; (Get-FileHash $n -Algorithm SHA256).Hash.ToLower() + '  ' + $n | Set-Content ($n+'.sha256') -Encoding ascii; Write-Host ('Done: ' + $n + '  (plus .sha256)')" || exit /b 1
exit /b 0


:clean
echo == Cleaning ==
if exist publish rmdir /s /q publish
if exist featherwall-*-win-x64.zip del /q featherwall-*-win-x64.zip
if exist featherwall-*-win-x64.zip.sha256 del /q featherwall-*-win-x64.zip.sha256
for /d %%p in (src\* tests\*) do (
    if exist "%%p\bin" rmdir /s /q "%%p\bin"
    if exist "%%p\obj" rmdir /s /q "%%p\obj"
)
echo Done.
exit /b 0
