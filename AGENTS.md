# Repository Guidelines

## Project Structure & Module Organization
VideoLoop/ hosts the net9.0-windows WPF app; App.xaml and App.xaml.cs bootstrap LibVLC while MainWindow.xaml and MainWindow.xaml.cs own layout and behaviour. bin/ and obj/ are generated outputs and stay untracked. publish-test/ stores a representative self-contained publish used to verify runtime assets. installer/ ships the PowerShell installers invoked by the root InstallVideoLoop.cmd and UninstallVideoLoop.cmd wrappers.

## Build, Test, and Development Commands
- `dotnet restore VideoLoop/VideoLoop.csproj` — restore NuGet packages and workloads.
- `dotnet build VideoLoop/VideoLoop.csproj` — compile the app and surface compile-time diagnostics.
- `dotnet run --project VideoLoop` — launch the loop player with console logging for quick manual checks.
- `dotnet publish VideoLoop/VideoLoop.csproj -c Release -o publish-test` — refresh the publish-test payload consumed by installer scripts.

## Coding Style & Naming Conventions
C# sources use file-scoped namespaces, four-space indentation, and `#nullable enable`. Private fields follow `_camelCase`, public members stay PascalCase, and constants use PascalCase as seen in MainWindow.xaml.cs. Favor focused methods, avoid region blocks, and run `dotnet format VideoLoop/VideoLoop.csproj` before committing. XAML keeps four-space indentation with multi-line attributes when a tag exceeds its line length.

## Testing Guidelines
Automated tests are not yet present; add new suites under a `VideoLoop.Tests/` sibling using xUnit or NUnit and wire them into `dotnet test`. Until then, publish to `publish-test`, launch VideoLoop.exe, validate loop playback, drag-and-drop, and window chrome, and attach the results to your PR. Note any manual regression gaps so future contributors can automate them.

## Commit & Pull Request Guidelines
History is sparse (`init`, `gitign`, `ignore`), so establish clarity: write concise, imperative commit summaries (e.g., `Add VLC player safety checks`) and reference issue IDs when applicable. Pull requests should state intent, list validation steps (manual or automated), and include screenshots or clips for UI-facing changes. Request an additional reviewer whenever touching installer or packaging assets.

## Installer & Packaging Notes
Keep publish-test/libvlc assets aligned with the LibVLC versions declared in VideoLoop.csproj. After publishing, validate with `powershell -File installer\InstallVideoLoop.ps1 -PublishRoot .\publish-test` in a clean VM, then confirm the uninstall script reverses the changes. Capture any new Windows prerequisites in the PR description so downstream agents stay informed.
