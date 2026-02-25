# VideoLoop

VideoLoop is a simple folder-based video looper.

## Platforms

- **Windows**: WPF GUI player (existing behavior)
- **Linux**: CLI launcher that loops a folder via `vlc`/`cvlc`/`mpv`

## Linux quick start

```bash
git clone https://github.com/SlimeQ/videoloop.git
cd videoloop
dotnet build VideoLoop/VideoLoop.csproj -c Release
# Run against current folder or pass a folder path:
dotnet run --project VideoLoop -- /path/to/videos
```

Linux mode requires at least one installed player:

- `vlc` or `cvlc`, or
- `mpv`

Ubuntu example:

```bash
sudo apt update
sudo apt install -y vlc
```

## Windows

Use the existing installer scripts:

- `InstallVideoLoop.cmd`
- `UninstallVideoLoop.cmd`
