<#
  一键生成安装包 DataAssistantSetup.exe
  步骤：
    1) 编译主程序 Release（Classic 离线版）
    2) 把 Release 产物打成 payload.zip
    3) 准备图标
    4) 编译安装程序（载荷内嵌）
    5) Authenticode 签名  ← 关键：Defender 的 ML 引擎会把"未签名 + 内嵌大载荷 + 自解压"
       误判为 Trojan:Win32/Bearfoos.A!ml，签名后走签名校验路径，误报消失
    6) 用 Defender 扫描校验（-DisableRemediation 只检测不隔离）

  用法：powershell -ExecutionPolicy Bypass -File build-installer.ps1
#>
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$root      = "G:\Model\WorkBuddy\AI编程助手"
$appDir    = Join-Path $root "DataAssistant"
$setupDir  = Join-Path $root "安装包\DataAssistantSetup"
$payload   = Join-Path $setupDir "payload.zip"
$outDir    = Join-Path $root "安装包"
$certFile  = Join-Path $setupDir "DataAssistantSetup.pfx"
# 证书密码从环境变量读取，避免明文写进公开仓库（本地构建前先设：setx DA_CERT_PWD 你的密码）
$certPwd   = $env:DA_CERT_PWD
if ([string]::IsNullOrEmpty($certPwd)) {
    throw "未设置证书密码环境变量 DA_CERT_PWD，请先执行: setx DA_CERT_PWD <你的密码> 后重开终端"
}
$certName  = "DataAssistant Setup"

Write-Host "=== 1/6 编译主程序 Release ===" -ForegroundColor Cyan
Push-Location $appDir
dotnet build "DataAssistant.Classic.csproj" -c Release --no-incremental 2>&1 |
    Out-String | ForEach-Object { $_ -replace "`0", "" } |
    Select-String -Pattern "error|个警告|个错误|已成功生成" | Select-Object -First 5
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "主程序编译失败" }
Pop-Location

# 用 Classic 版（离线自带 lib\NPOI 的 19 个 DLL，拷到任何电脑都能跑），产物直接在 bin\Release\ 下
$relDir = Join-Path $appDir "bin\Release"
if (-not (Test-Path (Join-Path $relDir "DataAssistant.exe"))) { throw "找不到主程序 Release 产物" }

Write-Host "=== 2/6 生成载荷 payload.zip ===" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
if (Test-Path $payload) { Remove-Item -Force $payload }

$files = Get-ChildItem $relDir -File | Where-Object { $_.Extension -ne ".pdb" }
$zip = [System.IO.Compression.ZipFile]::Open($payload, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        # 用正斜杠作为条目名（.NET 的 ZipArchive 在 Windows 上也认），保证跨平台解压正常
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $f.FullName, $f.Name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
Write-Host ("  载荷 {0} 个文件，{1:N1} MB" -f $files.Count, ((Get-Item $payload).Length / 1MB))

Write-Host "=== 3/6 准备图标 ===" -ForegroundColor Cyan
Copy-Item (Join-Path $appDir "appicon.ico") (Join-Path $setupDir "appicon.ico") -Force

Write-Host "=== 4/6 编译安装程序 ===" -ForegroundColor Cyan
Push-Location $setupDir
dotnet build "DataAssistantSetup.csproj" -c Release --no-incremental 2>&1 |
    Out-String | ForEach-Object { $_ -replace "`0", "" } |
    Select-String -Pattern "error|warning CS|个警告|个错误|已成功生成" | Select-Object -First 8
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "安装程序编译失败" }
Pop-Location

$setupExe = Join-Path $setupDir "bin\Release\net48\DataAssistantSetup.exe"
if (-not (Test-Path $setupExe)) { $setupExe = Join-Path $setupDir "bin\Release\DataAssistantSetup.exe" }
if (-not (Test-Path $setupExe)) { throw "找不到安装程序产物" }

# ---------- 签名 ----------
Write-Host "=== 5/6 Authenticode 签名 ===" -ForegroundColor Cyan
$cert = $null
# 优先复用已导出的 pfx，保证多次构建用同一张证书（换了证书用户的信任设置就失效了）
if (Test-Path $certFile) {
    try { $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certFile, $certPwd) } catch { }
}
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject "CN=$certName, O=DataAssistant" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(5) `
        -KeyUsage DigitalSignature `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    Export-PfxCertificate -Cert $cert -FilePath $certFile -Password (ConvertTo-SecureString $certPwd -AsPlainText -Force) | Out-Null
    Write-Host "  已生成新证书: $($cert.Thumbprint)"
}
# 让本机信任这把证书（否则签名状态是 UnknownError，Defender 仍可能走启发式路径）
# 注意：必须用 Export-Certificate 导出 .cer —— $cert.Export(Cert) 在对象带私钥时
# 会导出成 1 字节的无效文件（PowerShell 的坑）
$cerPath = Join-Path $setupDir "DataAssistantSetup.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
if ((Get-Item $cerPath).Length -lt 100) { throw "导出的 .cer 文件异常（仅 $((Get-Item $cerPath).Length) 字节）" }
foreach ($store in @("Cert:\LocalMachine\Root", "Cert:\CurrentUser\Root")) {
    try { Import-Certificate -FilePath $cerPath -CertStoreLocation $store | Out-Null } catch { }
}

$sig = Set-AuthenticodeSignature -FilePath $setupExe -Certificate $cert -HashAlgorithm SHA256
Write-Host "  签名状态: $($sig.Status)"
if ($sig.Status -ne "Valid") {
    Write-Host "  [警告] 签名未完全有效: $($sig.StatusMessage) —— 可能仍被 Defender 启发式误报" -ForegroundColor Yellow
} else {
    Write-Host "  证书: $($cert.Thumbprint)"
}

$final = Join-Path $outDir "DataAssistantSetup.exe"
Copy-Item $setupExe $final -Force
Copy-Item $cerPath (Join-Path $outDir "DataAssistantSetup.cer") -Force

# ---------- 产物完整性校验 ----------
Write-Host "=== 产物完整性校验 ===" -ForegroundColor Cyan
$problems = @()
if ((Get-Item $final).Length -lt 1MB) { $problems += "安装包体积异常（$((Get-Item $final).Length) 字节）" }
$cerOut = Join-Path $outDir "DataAssistantSetup.cer"
if ((Get-Item $cerOut).Length -lt 500) { $problems += "导出的 .cer 异常（$((Get-Item $cerOut).Length) 字节）" }
# 证书文件必须能被重新解析
try {
    $chk = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerOut)
    if ($chk.Subject -notmatch "DataAssistant") { $problems += "证书主题异常: $($chk.Subject)" }
} catch { $problems += "证书文件无法解析: $($_.Exception.Message)" }
# 安装包内的主程序名必须正确
try {
    $z = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $setupDir "payload.zip"))
    $hasExe = ($z.Entries | Where-Object { $_.Name -eq "DataAssistant.exe" }).Count -eq 1
    $z.Dispose()
    if (-not $hasExe) { $problems += "载荷内缺少 DataAssistant.exe" }
} catch { $problems += "载荷校验失败: $($_.Exception.Message)" }

if ($problems.Count -gt 0) {
    foreach ($p in $problems) { Write-Host "  [问题] $p" -ForegroundColor Red }
    throw "产物完整性校验未通过"
} else {
    Write-Host "  [OK] 安装包、证书、载荷均正常" -ForegroundColor Green
}

# ---------- Defender 校验 ----------
Write-Host "=== 6/6 Defender 扫描校验 ===" -ForegroundColor Cyan
$mp = Join-Path $env:ProgramData "Microsoft\Windows Defender\Platform\CurrentVersion\MpCmdRun.exe"
if (Test-Path $mp) {
    $res = & $mp -Scan -ScanType 3 -File $final -DisableRemediation 2>&1 |
           Out-String | Select-String -Pattern "found no threats|found \d+ threats" |
           ForEach-Object { $_.ToString().Trim() }
    if ($res -match "no threats") {
        Write-Host "  [OK] 未检测到威胁" -ForegroundColor Green
    } else {
        Write-Host "  [警告] $res" -ForegroundColor Yellow
    }
} else {
    Write-Host "  (未找到 MpCmdRun.exe，跳过校验)"
}

Write-Host ""
Write-Host "安装包已生成：" -ForegroundColor Green
Get-Item $final | Select-Object Name, @{n='大小(MB)';e={[math]::Round($_.Length/1MB,1)}}, LastWriteTime | Format-List
Write-Host "位置：$final"
Write-Host "随包证书：DataAssistantSetup.cer（用户机器首次运行需双击安装并信任，SmartScreen 提示点『更多信息→仍要运行』）"
Write-Host "用法：双击安装；静默安装：DataAssistantSetup.exe /S /dir=`"D:\DataAssistant`""
Write-Host "      卸载：运行安装目录下的 Uninstall.exe，或执行本程序 /uninstall"
