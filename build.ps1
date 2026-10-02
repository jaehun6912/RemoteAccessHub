<#
.SYNOPSIS
  RemoteAccessHub 빌드/검사/게시 스크립트.
.EXAMPLE
  .\build.ps1                 # 복원 + 빌드(Release) + 단위검사
  .\build.ps1 -Publish        # + artifacts\publish 에 실행 패키지 생성(framework-dependent, win-x64)
  .\build.ps1 -Publish -Installer  # + artifacts 에 설치 파일(setup.exe) 생성(Inno Setup 6 필요)
  .\build.ps1 -SelfTest       # + 모의 공유기 자체검사 실행(WebView2 필요, 창이 잠시 표시됨)
  .\build.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SelfTest,
    [switch]$Publish,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Find-Dotnet {
    # 1) PATH의 dotnet에 SDK가 있으면 사용
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $sdks = & $cmd.Source --list-sdks 2>$null
        if ($sdks -and ($sdks | Where-Object { $_ -match '^8\.' })) { return $cmd.Source }
    }
    # 2) 사용자 로컬 설치 (dotnet-install.ps1 -InstallDir %LOCALAPPDATA%\Microsoft\dotnet)
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) {
        $sdks = & $local --list-sdks 2>$null
        if ($sdks -and ($sdks | Where-Object { $_ -match '^8\.' })) { return $local }
    }
    throw ".NET 8 SDK를 찾을 수 없습니다. docs\빌드방법.md 를 참고해 설치하세요."
}

$dotnet = Find-Dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
Write-Host "dotnet: $dotnet" -ForegroundColor Cyan
& $dotnet --version

Write-Host "`n== restore + build ($Configuration) ==" -ForegroundColor Cyan
& $dotnet build RemoteAccessHub.sln -c $Configuration -nologo
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

if (-not $SkipTests) {
    Write-Host "`n== unit tests ==" -ForegroundColor Cyan
    & $dotnet test tests\RemoteAccessHub.Tests\RemoteAccessHub.Tests.csproj -c $Configuration --no-build -nologo
    if ($LASTEXITCODE -ne 0) { throw "단위검사 실패" }
}

if ($Publish) {
    Write-Host "`n== publish ==" -ForegroundColor Cyan
    $out = Join-Path $root 'artifacts\publish'
    & $dotnet publish src\RemoteAccessHub\RemoteAccessHub.csproj -c $Configuration -r win-x64 --self-contained false -o $out -nologo
    if ($LASTEXITCODE -ne 0) { throw "게시 실패" }
    $docs = Join-Path $out 'docs'
    New-Item -ItemType Directory -Force -Path $docs | Out-Null
    Copy-Item (Join-Path $root 'docs\*') $docs -Recurse -Force
    Copy-Item (Join-Path $root 'README.md') $out -Force
    # XML 문서 파일은 실행에 필요 없으므로 패키지에서 제외
    Get-ChildItem $out -Filter *.xml -File | Remove-Item -Force
    Write-Host "게시 폴더: $out"
    Write-Host "서명 상태: $((Get-AuthenticodeSignature (Join-Path $out 'RemoteAccessHub.exe')).Status)  (서명은 sign.ps1 참고)"

    $zip = Join-Path $root 'artifacts\RemoteAccessHub-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host "압축 파일: $zip"
}

if ($Installer) {
    Write-Host "`n== installer ==" -ForegroundColor Cyan
    $pub = Join-Path $root 'artifacts\publish'
    if (-not (Test-Path (Join-Path $pub 'RemoteAccessHub.exe'))) { throw "게시 폴더가 없습니다. -Publish 와 함께 실행하세요." }

    $iscc = $null
    foreach ($c in @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $c) { $iscc = $c; break }
    }
    if (-not $iscc) { $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($cmd) { $iscc = $cmd.Source } }
    if (-not $iscc) { throw "Inno Setup 6을 찾을 수 없습니다. 설치: winget install --id JRSoftware.InnoSetup" }

    $version = (Get-Item (Join-Path $pub 'RemoteAccessHub.exe')).VersionInfo.ProductVersion
    if ($version -match '^([0-9]+\.[0-9]+\.[0-9]+)') { $version = $Matches[1] }
    Write-Host "ISCC: $iscc  (버전 $version)"
    & $iscc "/DAppVersion=$version" (Join-Path $root 'installer\RemoteAccessHub.iss')
    if ($LASTEXITCODE -ne 0) { throw "설치 파일 생성 실패" }

    $setup = Join-Path $root "artifacts\RemoteAccessHub-setup-$version.exe"
    Write-Host "설치 파일: $setup"
    Write-Host "서명 상태: $((Get-AuthenticodeSignature $setup).Status)  (서명은 docs\코드서명과배포.md 참고)"
}

if ($SelfTest) {
    Write-Host "`n== self test (mock router, WebView2) ==" -ForegroundColor Cyan
    $exe = Join-Path $root "src\RemoteAccessHub\bin\$Configuration\net8.0-windows\RemoteAccessHub.exe"
    $result = Join-Path $root 'selftest-results.txt'
    $shots = Join-Path $root 'selftest-screenshots'
    $p = Start-Process -FilePath $exe -ArgumentList @('--selftest', $result, '--screenshots', $shots) -PassThru -Wait
    Get-Content $result
    if ($p.ExitCode -ne 0) { throw "자체검사 실패 (exit $($p.ExitCode))" }
}

Write-Host "`n완료" -ForegroundColor Green
