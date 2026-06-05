# Build and Publish

This page is for building, debugging, and publishing VoicemeeterDelay from source.

## Requirements

- Windows x64
- Visual Studio 2022
- **.NET desktop development** workload
- .NET 8 SDK
- Voicemeeter installed and running
- `VoicemeeterRemote64.dll` available from the default Voicemeeter install path, beside the app, on `PATH`, or selected from the app's API fallback picker

## Visual Studio Setup

1. Open Visual Studio Installer.
2. Install the **.NET desktop development** workload.
3. Open `VoicemeeterDelay.csproj` in Visual Studio.
4. Set the solution platform to `x64`.
5. Start Voicemeeter before running the app.
6. Press `F5` to debug, or `Ctrl+F5` to run without debugging.

The project targets `net8.0-windows`, uses WPF, and loads `VoicemeeterRemote64.dll` at runtime. It first tests the default Voicemeeter path under `Program Files (x86)\VB\Voicemeeter`. If the DLL is not found automatically, expand **API DLL fallback**, use Browse, and select it from the Voicemeeter install folder.

The app calls `VBVMR_GetVoicemeeterType` after logging in to the Remote API. It detects the running mixer as Standard, Banana, or Potato and trims the visible input/output buttons to the matching strip and bus layout.

## Build From PowerShell

```powershell
dotnet build .\VoicemeeterDelay.csproj -c Debug
```

## Run From Source

```powershell
dotnet run --project .\VoicemeeterDelay.csproj
```

## Publish Single EXE

Visual Studio can publish with the `win-x64-single-file` profile. From PowerShell:

```powershell
dotnet publish .\VoicemeeterDelay.csproj -c Release -r win-x64 -p:PublishProfile=win-x64-single-file
```

The publish profile creates a framework-dependent Windows x64 single-file executable, so the EXE stays small and the PC running it needs the .NET 8 Desktop Runtime installed. Voicemeeter still needs to be installed because the app loads the Voicemeeter Remote API DLL from the default install path, beside the app, on `PATH`, or from the saved fallback path.

## Publish To GitHub Releases

The repo includes a GitHub Actions workflow at `.github\workflows\release.yml`. To publish from GitHub, commit and push the project, then create and push a version tag:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The workflow publishes the Windows x64 single-file build and attaches both `VoicemeeterDelay-v1.0.0-win-x64.exe` and `VoicemeeterDelay-v1.0.0-win-x64.zip` to the GitHub Release.

You can also run the workflow manually from GitHub Actions with a tag value, or publish from this machine with GitHub CLI:

```powershell
gh auth login
.\scripts\Publish-GitHubRelease.ps1 -Tag v1.0.0
```

To build the release files without uploading them, run:

```powershell
.\scripts\Publish-GitHubRelease.ps1 -Tag v1.0.0 -NoUpload
```

Visual Studio also has a `GitHubRelease` publish profile. In Visual Studio, right-click the project, choose **Publish**, select **GitHubRelease**, and click **Publish**. The profile publishes the framework-dependent single-file EXE, packages it, and then calls `scripts\Publish-GitHubRelease.ps1` to create or update the GitHub Release.

The `GitHubRelease` profile uses `v$(Version)` as the release tag. To publish a different version, update the project `Version` property or edit `GitHubReleaseTag` in `Properties\PublishProfiles\GitHubRelease.pubxml`.

## Repository Artwork

Repository artwork is in `Assets`. Use `Assets/VoicemeeterDelaySocialPreview.png` as the GitHub repository social preview image.
