# Publishing

This repository is prepared as a clean, public snapshot. It intentionally has no
bundled third-party translation data, personal screenshots, API keys or
modpack data.

## 1. Create the public repository

Create an empty public repository on GitHub, for example:

```text
Chashao-mao/PackDependencyTool
```

Do not initialize it with a README if the current branch is going to be pushed
directly.

## 2. Push the clean branch

The local `public` branch is an orphan branch with no earlier private history.

```bash
git remote add origin https://github.com/Chashao-mao/PackDependencyTool.git
git push -u origin public:main
git push origin v1.0.0
```

After the push, change the default branch to `main` in the GitHub settings.

## 3. Fill in update.json

Replace every `Chashao-mao` placeholder in `update.json`:

```json
{
  "version": "1.0.0",
  "releaseUrl": "https://github.com/Chashao-mao/PackDependencyTool/releases/tag/v1.0.0",
  "downloadUrl": "https://github.com/Chashao-mao/PackDependencyTool/releases/download/v1.0.0/PackDependencyTool.exe",
  "sha256": "<64-char sha256 of PackDependencyTool.exe>",
  "notes": "First public release",
  "publishedAt": "2026-10-23T00:00:00Z"
}
```

The default feed users can enter in Settings is:

```text
https://raw.githubusercontent.com/Chashao-mao/PackDependencyTool/main/update.json
```

## 4. Build the release

```powershell
dotnet publish src/PackDepTool.App/PackDepTool.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o dist
```

Upload `dist/PackDependencyTool.exe` to the `v1.0.0` GitHub Release.

Compute the checksum and put it into `update.json`:

```powershell
(Get-FileHash -Algorithm SHA256 dist\PackDependencyTool.exe).Hash.ToLower()
```

The auto-update path refuses to execute a download when `sha256` is missing or
does not match.

## 6. Git credentials

The machine is configured to use Git Credential Manager:

```bash
git config --global credential.helper manager
```

On first push, GCM opens a browser/device login and stores the credential in
Windows Credential Manager. Do not paste a PAT into the chat or a source file.

If `gh` is available:

```bash
gh auth login
gh repo create PackDependencyTool --public --source=. --remote=origin
git push -u origin public:main
```

If `gh` is not installed, create an empty public repository in the browser,
add its HTTPS URL with `git remote add origin ...`, and push; GCM handles the
credentials.

## 5. Never publish

- any third-party translation index or personal data
- personal screenshots or `data/` directories
- CurseForge/Modrinth API keys
- historical commits from before the public orphan branch
