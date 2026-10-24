# Security

Please report security issues privately through GitHub Security Advisories
instead of opening a public issue.

Do not include API keys, CurseForge/Modrinth tokens, personal paths, modpack
archives or personal configuration files in reports.

The application stores settings and cached metadata under the executable's
`data` directory. API keys are protected with Windows DPAPI and are never
written to logs. Automatic update execution is opt-in, requires a SHA256 checksum in the
manifest, and uses an in-app updater with backup/rollback. The project does not
ship a code-signing certificate; a compromised HTTPS feed remains a risk.
