param(
    [string]$ExePath = "dist\PackDependencyTool-win-x64\PackDependencyTool.exe",
    [string]$Thumbprint = "132BDA330ACF56C47D095F50C5A395A2CB9A9D5A"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $ExePath)) { throw "找不到 exe：$ExePath" }

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Thumbprint -eq $Thumbprint } |
    Select-Object -First 1
if ($null -eq $cert) { throw "当前用户证书存储中找不到证书 $Thumbprint" }

$signature = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256
$signature | Select-Object Status, StatusMessage,
    @{ Name = 'Signer'; Expression = { $_.SignerCertificate.Subject } },
    @{ Name = 'Thumbprint'; Expression = { $_.SignerCertificate.Thumbprint } } |
    Format-List

$hash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "SHA256: $hash"
Write-Host "把 SHA256 和 signerThumbprint 写入 update.json。"
if ($signature.Status -ne 'Valid') {
    Write-Warning "证书未受公开信任（自签名），Windows 会显示未知发布者；updater 使用固定指纹校验。"
}
