# ScanWithWeb - Agent Notes

This repository contains a Windows desktop service (WinForms) that exposes local scanner capabilities over WebSockets for browser clients.

## Structure

- `ScanWithWeb/` — main .NET app (WinForms, WebSocket server, scanner integrations)
- `ScanWithWeb_setup/` — Inno Setup scripts + packaging helpers for installers
- `packages/scanwith-web-sdk/` — TypeScript SDK for web clients
- `.github/workflows/build-release.yml` — CI build/publish flow (Windows build + optional GitHub release)

## Build & Run

- Main project: `ScanWithWeb/ScanWithWeb.csproj`
  - Target: `net8.0-windows`
  - WinForms: `UseWindowsForms=true`
  - Publishing is intended for Windows (`win-x64` / `win-x86`) and single-file (`PublishSingleFile=true`).

Typical Windows publish commands:

- `dotnet publish ScanWithWeb/ScanWithWeb.csproj -c Release -r win-x64 -o dist/win-x64 --self-contained true`
- `dotnet publish ScanWithWeb/ScanWithWeb.csproj -c Release -r win-x86 -o dist/win-x86 --self-contained true`

Note: building on non-Windows hosts may require regenerating NuGet restore assets locally. Avoid committing build outputs (`bin/`, `obj/`, `.vs/`).

## Runtime Notes

- WebSocket endpoints (default):
  - WS: `ws://localhost:8180`
  - WSS: `wss://localhost:8181`
- SSL certificate handling:
  - Auto-generates a self-signed certificate and (optionally) installs it into the current user’s trusted root store.
  - Certificate and logs are written under `%LocalAppData%\\ScanWithWeb` on Windows.

## Versioning

- Source of truth is `ScanWithWeb/ScanWithWeb.csproj` (`<Version>...</Version>`).
- Some code paths may also embed version strings (e.g., UI label or TWAIN identity). When bumping versions, search for the current version and update consistently.

## Repository Hygiene

- Do not commit `bin/`, `obj/`, `.vs/`, logs, or `dist/`.
- `packages/` at repo root is used for the SDK workspace; do not remove it.

