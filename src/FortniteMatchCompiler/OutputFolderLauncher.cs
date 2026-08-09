using System.Diagnostics;

namespace FortniteMatchCompiler;

internal static class OutputFolderLauncher
{
    public static string? ResolveFolder(
        string? lastOutputPath,
        string? configuredOutputFolder)
    {
        if (!string.IsNullOrWhiteSpace(lastOutputPath) && File.Exists(lastOutputPath))
        {
            var parentFolder = Path.GetDirectoryName(Path.GetFullPath(lastOutputPath));
            if (!string.IsNullOrWhiteSpace(parentFolder))
            {
                return parentFolder;
            }
        }

        if (string.IsNullOrWhiteSpace(configuredOutputFolder))
        {
            return null;
        }

        return Path.GetFullPath(configuredOutputFolder.Trim());
    }

    public static ProcessStartInfo CreateStartInfo(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        return new ProcessStartInfo(Path.GetFullPath(folderPath))
        {
            UseShellExecute = true,
            Verb = "open"
        };
    }
}
