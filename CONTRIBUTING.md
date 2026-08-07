# Contributing

Thank you for considering a contribution to Collaborative Whiteboard LAN.

## Before you start

- Search existing issues before opening a new one.
- Use a bug report for reproducible defects and a feature request for proposed behavior.
- For security concerns, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.
- Keep changes focused. Large architectural changes should be discussed in an issue first.

## Development setup

You need Windows 10 build 19041 or later and the .NET 10 SDK.

```powershell
git clone https://github.com/qivarnq3g/collaborative-whiteboard-lan.git
cd collaborative-whiteboard-lan
dotnet restore .\066206005044_BTL_CollaborativeWhiteboardLAN.slnx
dotnet build .\066206005044_BTL_CollaborativeWhiteboardLAN.slnx --configuration Release --no-restore
```

Run the server and at least two clients when testing synchronization or moderation behavior.

## Pull requests

1. Create a branch from `main`.
2. Make the smallest coherent change that solves the issue.
3. Preserve nullable-reference-type warnings and existing protocol validation.
4. Build the full solution in Release mode.
5. Test affected multi-client behavior manually when automated coverage is unavailable.
6. Update documentation when behavior, configuration, or protocol details change.
7. Complete the pull request checklist and explain any untested path.

Use clear commit messages written in the imperative mood, for example `fix: reject oversized discovery responses`.

By contributing, you agree that your contribution is licensed under the repository's MIT License.
