# Signing

This release is signed with a local code-signing certificate.

- Subject: `CN=Chashao-mao, O=PackDependencyTool, OU=Self-signed`
- SHA-1 thumbprint: `132BDA330ACF56C47D095F50C5A395A2CB9A9D5A`
- Public certificate: `PackDependencyTool-selfsigned.cer`
- Private key: kept in the Windows certificate store, **not** in this repository

Windows may show an unknown publisher because the certificate is not issued by
a publicly trusted CA. The updater pins this signer thumbprint, so a downloaded
release signed by a different certificate is rejected even if its SHA256 matches
a compromised manifest.

For public releases without a SmartScreen warning, replace this certificate with
a CA-issued code-signing certificate. If the signer changes, update
`ExpectedSignerThumbprint` in `src/PackDepTool.App/Services/UpdateInstaller.cs`.

## Sign

```powershell
powershell -ExecutionPolicy Bypass -File tools\sign-release.ps1
```

Then update `update.json` with the printed SHA256. The private key is not
exportable to the repository and must be backed up separately.
