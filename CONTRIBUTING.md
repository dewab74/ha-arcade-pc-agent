# Contributing

Thanks for helping. Issues and pull requests are welcome.

## Setup

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or
   newer) and [pre-commit](https://pre-commit.com/). The PowerShell hook
   needs [PowerShell 7](https://github.com/PowerShell/PowerShell) (`pwsh`).
2. Clone, then install the git hooks. Hooks live in `.git/hooks/`, which is
   not cloned, so this is needed once per clone:

   ```bash
   pre-commit install
   pre-commit run --all-files
   ```

3. Build and test (`scripts/build-installer.ps1` builds the installer on
   Windows with [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

   ```bash
   dotnet build ArcadePcAgent.slnx
   dotnet test tests/ArcadePcAgent.Core.Tests
   ```

`ArcadePcAgent.Core` and its tests build and run on Windows, macOS and
Linux. The Windows app (`src/ArcadePcAgent`, WinForms tray, Core Audio) also
builds on any OS, but only runs on Windows.

## Rules

- **Conventional Commits** (`feat: ...`, `fix: ...`, `docs: ...`). The
  commit-msg hook enforces it, and the release notes are built from it.
- The hooks run `dotnet format --verify-no-changes`, `dotnet build
  -warnaserror`, the tests, shellcheck/shfmt, markdownlint, a PowerShell
  parse check, gitleaks and detect-secrets. Don't bypass them with
  `--no-verify`. If a secret scanner fires on a real credential, rotate it.
- New behaviour comes with tests. Logic goes in Core, where it can be tested;
  keep the Windows side thin.
- Commands must stay fixed-function: nothing from MQTT may become a path, a
  command line or a process name.
- Update `CHANGELOG.md` under `[Unreleased]`.

## CI

| Workflow | Runs | Does |
| --- | --- | --- |
| `ci.yml` | push to `main`, pull requests | Locked restore, build with warnings as errors, and tests on Ubuntu and Windows. On Windows it also publishes the exe and builds the Inno Setup installer, uploaded as build artifacts |
| `pre-commit.yml` | push, pull requests | The same hooks as your machine, as a backstop for `--no-verify` |
| `codeql.yml` | push, pull requests, weekly | CodeQL security analysis of the C# |
| `dependency-review.yml` | pull requests | Blocks new dependencies with known vulnerabilities or incompatible licenses |
| `release.yml` | tag `v*` | Builds, tests, publishes and builds the installer; then attaches `ArcadePcAgent-Setup-<version>.exe`, the exe, `install-arcade-pc-agent.ps1` and `SHA256SUMS` to a GitHub release, with build provenance attestations |

Dependabot keeps NuGet packages and GitHub Actions current.

## Releasing

1. Test the build on a real Windows PC. Check that the tray turns green, that
   mute and volume work both ways, and that Settings saves and reconnects.
2. Set `VERSION`. In `CHANGELOG.md`, rename `[Unreleased]` to
   `[X.Y.Z] - YYYY-MM-DD`, then commit (`chore(release): X.Y.Z`) and push to
   `main`.
3. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`. The release
   workflow checks that the tag matches `VERSION`, and that `CHANGELOG.md`
   has a section for it, before publishing.
