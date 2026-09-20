<#
.SYNOPSIS
  게시된 RemoteAccessHub.exe에 코드 서명을 적용하고 결과를 확인합니다.

.DESCRIPTION
  이 스크립트는 "이미 가지고 있는" 코드 서명 인증서로 서명만 수행합니다.
  인증서를 만들어 주지 않으며, 자체 서명 인증서로 Smart App Control을 통과시키지도 못합니다.
  자세한 배경은 docs\코드서명과배포.md 를 읽으세요.

.PARAMETER Thumbprint
  인증서 저장소(Cert:\CurrentUser\My)에 있는 코드 서명 인증서의 지문.

.PARAMETER PfxPath
  PFX 파일 경로. 암호는 프롬프트로 입력받습니다(명령줄에 넣지 마세요).

.EXAMPLE
  .\sign.ps1 -Thumbprint ABCD1234...
  .\sign.ps1 -PfxPath C:\secure\mycert.pfx
#>
[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(ParameterSetName = 'Store', Mandatory)][string]$Thumbprint,
    [Parameter(ParameterSetName = 'Pfx', Mandatory)][string]$PfxPath,
    [string]$Path = "artifacts\publish\RemoteAccessHub.exe",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not (Test-Path $Path)) { throw "서명할 파일이 없습니다: $Path  (먼저 .\build.ps1 -Publish 실행)" }

Write-Host "서명 전 상태: $((Get-AuthenticodeSignature $Path).Status)"

if ($PSCmdlet.ParameterSetName -eq 'Store') {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $Thumbprint }
    if (-not $cert) { throw "지문 $Thumbprint 인증서를 Cert:\CurrentUser\My 에서 찾을 수 없습니다." }
} else {
    if (-not (Test-Path $PfxPath)) { throw "PFX 파일이 없습니다: $PfxPath" }
    $pw = Read-Host -AsSecureString "PFX 암호"
    $cert = Get-PfxCertificate -FilePath $PfxPath -Password $pw
}

if (-not $cert.HasPrivateKey) { throw "선택한 인증서에 개인 키가 없습니다." }

# SHA-256 + RFC3161 타임스탬프. 타임스탬프가 없으면 인증서 만료 후 서명이 무효가 됩니다.
$result = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
"서명 결과: $($result.Status) $($result.StatusMessage)"
"서명자    : $($result.SignerCertificate.Subject)"
"타임스탬프: $($result.TimeStamperCertificate.Subject)"

$verify = Get-AuthenticodeSignature $Path
if ($verify.Status -ne 'Valid') { throw "서명 검증 실패: $($verify.Status) $($verify.StatusMessage)" }

Write-Host ""
Write-Host "서명이 유효합니다. 다만 Smart App Control은 '유효한 서명'만으로 통과되지 않습니다." -ForegroundColor Yellow
Write-Host "docs\코드서명과배포.md 의 '무엇이 더 필요한가' 항목을 확인하세요." -ForegroundColor Yellow
