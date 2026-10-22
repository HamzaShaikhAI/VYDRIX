# Contributing

Thanks for helping. A few ground rules keep CLOUDPLAY X honest and safe:

- **No DRM, anti-cheat, launcher or ownership bypasses.** Pull requests that add them are closed.
- **No fabricated numbers.** Metrics, benchmarks and "game works" claims must come from a real, reproducible run.
  Say what was tested and what was not.
- **Never delete cloud data**, and never delete user save files to free cache space.
- Keep secrets out of the repository (see [SECURITY.md](SECURITY.md)).

## Development

Requirements: Windows 10/11 x64, .NET 10 SDK, WinFsp (for integration tests), PowerShell.

```bash
powershell -ExecutionPolicy Bypass -File installer/fetch-rclone.ps1
```

```bash
dotnet build CloudPlayX.slnx -c Release
```

```bash
dotnet test tests/CloudPlayX.Tests
```

Integration tests (`tests/CloudPlayX.IntegrationTests`) mount real drives and take several minutes. Please run them
when you change mounting, caching or streaming code, and include the results in your pull request.

To build the installer (`dist/CloudPlayX-<version>-setup.exe`), install Inno Setup 6
(`winget install JRSoftware.InnoSetup`) and run `installer/build-release.ps1`.
