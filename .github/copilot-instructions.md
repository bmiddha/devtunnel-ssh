# Copilot instructions for dtssh

`dtssh` makes `ssh <alias>` work through a Microsoft Dev Tunnel with no manual SSH
setup. It's a single **.NET 10 NativeAOT** CLI (single self-contained binary).

## Project layout
- `Dtssh.csproj` — `<Version>` (line ~16) is the **single source of truth** for the
  version; a `SetGitRevision` MSBuild target appends the git short SHA to
  `InformationalVersion`. `PublishAot=true`.
- `src/Program.cs` — entry point + command dispatch; `Program.Version` reads
  `AssemblyInformationalVersionAttribute`.
- `src/Commands/` — `login`, `host`, `proxy`, `discover`/client, `service`.
- `src/` also: `Connections/` (Dev Tunnels SDK in-process host+client),
  `Keys/`, `Ssh/`, `Discovery/`, `Service/`, `Wsl/`, `Json/`, `Auth/`, `Infra/`.
- `scripts/install-release.{sh,ps1}` — the only installers (download from GitHub
  Releases + checksum). The `.ps1` is cross-platform (PS 5.1 + PS 7 on any OS).
- `.github/workflows/` — `build.yml` (reusable matrix build), `release.yml`
  (dispatch: git-cliff version bump → opens a `chore(release)` PR),
  `release-publish.yml` (on release-PR merge: tag → build → publish),
  `conventional.yml` (PR check).

## NativeAOT rules (important)
- All JSON must go through the source-gen `DtsshSerializerContext` (`src/Json/`);
  no reflection-based serialization.
- The Dev Tunnels SDK isn't AOT-safe by default: its JSON options are patched with
  a source-gen context, `TrimmerRoots.xml` preserves the SSH assemblies, and
  polymorphic tunnel-endpoint types are explicitly registered. Don't remove these.
- Expect IL2104/IL3053 trim/AOT warnings from the SDK — those are known, not errors.
- Build/publish: `dotnet publish -c Release -r <rid> -p:PublishAot=true`.

## Conventions
- **Commits: Conventional Commits**, enforced on PRs. Types map to emoji
  changelog groups (via `.config/cliff.toml`):
  `feat:` 🚀, `fix:` 🐛, `perf:` ⚡, `refactor:` 🚜, `docs:` 📚, `test:` 🧪,
  `ci:`/`build:` 👷, `style:` 🎨, `chore:`/`revert:` ⚙️. The repo does **not**
  squash-merge — each commit feeds the git-cliff changelog
  (`.config/cliff.toml` → `CHANGELOG.md`). Make normal per-change commits and push
  non-force; do not rewrite history.
- End commit messages with the `Co-authored-by: Copilot <...>` trailer.
- Bump releases via the `release` workflow, never by hand-editing versions in code.
- Validate workflow changes with `actionlint`.

## Design notes
- Two auth layers: the tunnel (login/connect token) **and** SSH public-key auth.
  "No extra SSH auth" = dtssh provisions the key for you, not that auth is disabled.
- `sshd` binds to loopback; the tunnel is the only ingress. Host keys are **pinned**
  on discovery (no trust-on-first-use) and self-heal from account-private metadata.
- The `devtunnel` CLI is used only for login/tunnel-create/token issuance and is
  auto-downloaded; tunnel host+client connections run in-process via the SDK.
