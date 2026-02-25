# VideoLoop

VideoLoop is a simple folder-based video looper.

## Platforms

- **Windows**: WPF GUI player (existing behavior)
- **Linux**: CLI launcher that loops a folder via `vlc`/`cvlc`/`mpv`

## Linux quick start

One-liner install + run:

```bash
sudo apt update && sudo apt install -y dotnet-sdk-9.0 vlc git && git clone https://github.com/SlimeQ/videoloop.git && cd videoloop && dotnet build VideoLoop/VideoLoop.csproj -c Release && dotnet run --project VideoLoop --framework net9.0 -- /path/to/videos
```

Step-by-step:

```bash
git clone https://github.com/SlimeQ/videoloop.git
cd videoloop
dotnet build VideoLoop/VideoLoop.csproj -c Release
# Run against current folder or pass a folder path:
dotnet run --project VideoLoop --framework net9.0 -- /path/to/videos
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
