#if !WINDOWS
using System.Diagnostics;

namespace VideoLoop;

internal static class LinuxMain
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mpg", ".mpeg", ".mkv", ".webm"
    };

    public static int Main(string[] args)
    {
        var folder = ResolveFolder(args);
        if (folder is null)
        {
            Console.Error.WriteLine("Usage: videoloop <folder-with-videos>");
            return 2;
        }

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No supported videos found in: {folder}");
            return 3;
        }

        var m3uPath = Path.Combine(Path.GetTempPath(), $"videoloop-{Guid.NewGuid():N}.m3u");
        File.WriteAllLines(m3uPath, files);

        try
        {
            var player = FindPlayerBinary();
            if (player is null)
            {
                Console.Error.WriteLine("No video player found. Install VLC (vlc/cvlc) or mpv.");
                return 4;
            }

            Console.WriteLine($"VideoLoop Linux mode using {player} for {files.Count} files.");

            var psi = new ProcessStartInfo
            {
                FileName = player,
                UseShellExecute = false
            };

            if (player.EndsWith("mpv", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("--fs");
                psi.ArgumentList.Add("--loop-playlist=inf");
                psi.ArgumentList.Add($"--playlist={m3uPath}");
            }
            else
            {
                // vlc / cvlc
                psi.ArgumentList.Add("--fullscreen");
                psi.ArgumentList.Add("--loop");
                psi.ArgumentList.Add(m3uPath);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine("Failed to start player process.");
                return 5;
            }

            proc.WaitForExit();
            return proc.ExitCode;
        }
        finally
        {
            try { File.Delete(m3uPath); } catch { }
        }
    }

    private static string? ResolveFolder(string[] args)
    {
        if (args.Length > 0)
        {
            var arg = Environment.ExpandEnvironmentVariables(args[0].Trim('"'));
            if (Directory.Exists(arg))
            {
                return Path.GetFullPath(arg);
            }
        }

        var cwd = Environment.CurrentDirectory;
        return Directory.Exists(cwd) ? cwd : null;
    }

    private static string? FindPlayerBinary()
    {
        foreach (var bin in new[] { "vlc", "cvlc", "mpv" })
        {
            if (CommandExists(bin)) return bin;
        }

        return null;
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add($"command -v {command}");
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
#endif
