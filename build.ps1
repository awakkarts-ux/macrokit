# build.ps1 — сборка MacroKit
#
# Внешних зависимостей нет: компилятор Roslyn от VS Build Tools либо встроенный csc,
# библиотеки — из .NET Framework, который есть в каждой Windows.

param(
    # Версия сборки. По умолчанию — из файла VERSION в корне.
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src  = Join-Path $root 'src'
$bin  = Join-Path $root 'bin'
$out  = Join-Path $bin 'MacroKit.exe'

if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

# ---- компилятор ----
$csc = $null
$roslynCandidates = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe",
    "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe",
    "${env:ProgramFiles(x86)}\MSBuild\*\Bin\Roslyn\csc.exe"
)
foreach ($c in $roslynCandidates) {
    $hit = Get-Item $c -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { $csc = $hit.FullName; break }
}
if (-not $csc) {
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    Write-Host "Roslyn не найден, беру встроенный csc (C# 5) — возможны ошибки." -ForegroundColor Yellow
}
# /langversion:latest встроенный csc не понимает и падает с CS1617
$langArg = if ($csc -like '*Roslyn*') { @('/langversion:latest') } else { @() }

# ---- библиотеки ----
$refDir = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
if (-not (Test-Path (Join-Path $refDir 'System.dll'))) {
    $refDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
}

$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Web.Extensions.dll'
) | ForEach-Object { '/r:' + (Join-Path $refDir $_) }

# ---- иконка ----
$iconPath = Join-Path $root 'icon.ico'
$iconArg = @()
if (-not (Test-Path $iconPath)) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\make-icon.ps1')
}
if (Test-Path $iconPath) {
    # win32icon — для Проводника и ярлыка, resource — для окна и панели задач
    $iconArg = @("/win32icon:$iconPath", "/resource:$iconPath,MacroKit.icon.ico")
}

# ---- шрифты интерфейса ----
# Onest и JetBrains Mono вкладываются в exe ресурсами (лицензия SIL OFL —
# fonts\ui\OFL-*.txt), программа регистрирует их сама: ставить в систему не нужно.
$fontArgs = Get-ChildItem -Path (Join-Path $root 'fonts\ui') -Filter *.ttf -ErrorAction SilentlyContinue |
    ForEach-Object { "/resource:$($_.FullName),MacroKit.Fonts.$($_.Name)" }

# ---- версия ----
# Один источник правды — файл VERSION. Из него же берут номер установщик
# (installer\MacroKit.iss) и выпуск на GitHub; программа сравнивает его с
# последним релизом, когда проверяет обновления.
if (-not $Version) {
    $versionFile = Join-Path $root 'VERSION'
    if (Test-Path $versionFile) { $Version = (Get-Content $versionFile -Raw).Trim() } else { $Version = '0.0.0' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Host "  Версия '$Version' не похожа на X.Y.Z" -ForegroundColor Red
    exit 1
}

$obj = Join-Path $root 'obj'
if (-not (Test-Path $obj)) { New-Item -ItemType Directory -Path $obj | Out-Null }
$assemblyInfo = Join-Path $obj 'AssemblyInfo.g.cs'
@"
// Создаётся build.ps1 из файла VERSION — руками не править.
using System.Reflection;
[assembly: AssemblyTitle("MacroKit")]
[assembly: AssemblyProduct("MacroKit")]
[assembly: AssemblyDescription("Макросы для клавиатуры")]
[assembly: AssemblyCompany("awakkarts-ux")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Path $assemblyInfo -Encoding UTF8

# ---- исходники ----
$files = @(Get-ChildItem -Path $src -Recurse -Filter *.cs | ForEach-Object { $_.FullName }) + $assemblyInfo

Write-Host ""
Write-Host "  Компилятор : $csc" -ForegroundColor DarkGray
Write-Host "  Библиотеки : $refDir" -ForegroundColor DarkGray
Write-Host "  Версия     : $Version" -ForegroundColor DarkGray
Write-Host "  Файлов     : $($files.Count)" -ForegroundColor DarkGray
Write-Host ""

$cscArgs = @(
    '/nologo'
    '/preferreduilang:en-US'
    '/codepage:65001'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    '/debug-'
    "/out:$out"
) + $langArg + $iconArg + $fontArgs + $refs + $files

& $csc $cscArgs
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host ""
    Write-Host "  Сборка упала (код $code)" -ForegroundColor Red
    Write-Host ""
    exit $code
}

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host ""
Write-Host "  Готово: $out  ($size КБ)" -ForegroundColor Green

# ---- смоук-тест ----
#
# Собирается здесь же, иначе тихо отстаёт от кода: однажды он две недели
# проверял старый движок и показывал зелёное. Точка входа у теста своя,
# поэтому Program.cs из набора исходников исключается.
$smokeSrc = Join-Path $root 'tests\Smoke.cs'
if (Test-Path $smokeSrc) {
    $smokeOut = Join-Path $bin 'SmokeTest.exe'
    $smokeFiles = @($files | Where-Object { (Split-Path $_ -Leaf) -ne 'Program.cs' }) + $smokeSrc

    $smokeArgs = @(
        '/nologo'
        '/preferreduilang:en-US'
        '/codepage:65001'
        '/target:exe'
        '/platform:x64'
        '/optimize+'
        '/debug-'
        "/out:$smokeOut"
    ) + $langArg + $refs + $smokeFiles

    & $csc $smokeArgs

    if ($LASTEXITCODE -eq 0) {
        $smokeSize = [math]::Round((Get-Item $smokeOut).Length / 1KB, 1)
        Write-Host "  Готово: $smokeOut  ($smokeSize КБ)" -ForegroundColor Green
        Write-Host ""
        Write-Host "  Проверить движок: bin\SmokeTest.exe" -ForegroundColor DarkGray
    } else {
        Write-Host ""
        Write-Host "  Смоук-тест не собрался (код $LASTEXITCODE)" -ForegroundColor Red
        Write-Host "  Программа собрана и рабочая, но проверить её нечем." -ForegroundColor Red
        Write-Host ""
        exit $LASTEXITCODE
    }
}

Write-Host ""
