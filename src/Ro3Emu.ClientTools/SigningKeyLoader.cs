namespace Ro3Emu.ClientTools;

/// <summary>
/// Loads channel-signing private keys from an external PEM file or CI environment variable, refusing keys stored inside
/// a git/source repository or inside the HTTP-served publish repository directory (including through symlinks or junctions).
/// </summary>
internal static class SigningKeyLoader
{
    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    internal static string? Load(string? cliKeyFile, string? publishRepoDir, Func<string, string?>? getEnv = null)
    {
        if (cliKeyFile != null && string.IsNullOrWhiteSpace(cliKeyFile))
        {
            throw new ArgumentException("--signing-key requires a non-empty path to a PEM file outside the repository and publish directory.", nameof(cliKeyFile));
        }

        getEnv ??= Environment.GetEnvironmentVariable;
        string? filePath = cliKeyFile ?? getEnv("RO3EMU_CHANNEL_SIGNING_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            string full = Path.GetFullPath(filePath);
            string resolved = ResolveRealPath(full);
            EnsureSafeLocation(full, resolved, publishRepoDir);
            return File.ReadAllText(resolved);
        }

        string? envPem = getEnv("RO3EMU_CHANNEL_SIGNING_KEY");
        return string.IsNullOrWhiteSpace(envPem) ? null : envPem;
    }

    private static void EnsureSafeLocation(string fullKeyPath, string resolvedKeyPath, string? publishRepoDir)
    {
        if (IsInsideSourceRepo(fullKeyPath) || IsInsideSourceRepo(resolvedKeyPath))
        {
            throw new InvalidOperationException(
                "Refusing to load channel signing private key from inside a repository; move it outside the repo or use RO3EMU_CHANNEL_SIGNING_KEY.");
        }

        if (!string.IsNullOrWhiteSpace(publishRepoDir))
        {
            string fullPublish = Path.GetFullPath(publishRepoDir);
            string resolvedPublish = ResolveRealPath(fullPublish);
            if (IsInsideDirectory(fullKeyPath, fullPublish)
                || IsInsideDirectory(fullKeyPath, resolvedPublish)
                || IsInsideDirectory(resolvedKeyPath, fullPublish)
                || IsInsideDirectory(resolvedKeyPath, resolvedPublish))
            {
                throw new InvalidOperationException(
                    "Refusing to load channel signing private key from inside the publish repository directory (it is served over HTTP).");
            }
        }
    }

    internal static string ResolveRealPath(string path)
    {
        string current = Path.GetFullPath(path);
        for (int hop = 0; hop < 40; hop++)
        {
            string? root = Path.GetPathRoot(current);
            if (string.IsNullOrEmpty(root)) return current;
            string rest = current[root.Length..];
            string[] parts = rest.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            string built = root;
            bool expanded = false;
            for (int i = 0; i < parts.Length; i++)
            {
                built = Path.Combine(built, parts[i]);
                FileSystemInfo? target = TryResolveLink(built);
                if (target == null) continue;
                string targetFull = StripExtendedPrefix(target.FullName);
                for (int j = i + 1; j < parts.Length; j++)
                {
                    targetFull = Path.Combine(targetFull, parts[j]);
                }
                current = Path.GetFullPath(targetFull);
                expanded = true;
                break;
            }
            if (!expanded) return current;
        }
        throw new InvalidOperationException($"Too many symbolic links while resolving '{path}'.");
    }

    private static FileSystemInfo? TryResolveLink(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Directory.ResolveLinkTarget(path, returnFinalTarget: false);
            }
            if (File.Exists(path))
            {
                return File.ResolveLinkTarget(path, returnFinalTarget: false);
            }
            var fileInfo = new FileInfo(path);
            if (fileInfo.LinkTarget != null)
            {
                return fileInfo.ResolveLinkTarget(returnFinalTarget: false);
            }
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.LinkTarget != null)
            {
                return dirInfo.ResolveLinkTarget(returnFinalTarget: false);
            }
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..];
        }
        return path;
    }

    private static bool IsInsideSourceRepo(string path)
    {
        string? start = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        for (var dir = start == null ? null : new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx")))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(fullPath, fullDir, comparison)) return true;
        string prefix = fullDir.EndsWith(Path.DirectorySeparatorChar) ? fullDir : fullDir + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison);
    }
}
