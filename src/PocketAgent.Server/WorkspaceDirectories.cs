namespace PocketAgent;

public sealed record DirectoryEntry(string Name, string Path);
public sealed record DirectoryListing(string Path, string? Parent, string DefaultDirectory, DirectoryEntry[] Roots, DirectoryEntry[] Directories, bool HasMore);

public sealed class WorkspaceDirectories(HostSettings settings)
{
    public string Resolve(string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? settings.RepositoryPath : path.Trim();
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ApiException(400, "Choose an absolute folder path on a local drive.");
        try
        {
            var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            if (OperatingSystem.IsWindows() && new DriveInfo(System.IO.Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
                throw new ApiException(400, "Choose a folder on a local drive.");
            if (!Directory.Exists(full)) throw new ApiException(404, "The selected folder does not exist or is inaccessible.");
            return full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { throw new ApiException(400, "The selected local folder is invalid or inaccessible."); }
    }

    public DirectoryListing Browse(string? path)
    {
        var full = Resolve(path);
        try
        {
            var directories = new DirectoryInfo(full).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                .Take(501).OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var roots = DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(drive => new DirectoryEntry(drive.Name, drive.RootDirectory.FullName)).ToArray();
            return new(full, Directory.GetParent(full)?.FullName, settings.RepositoryPath, roots,
                directories.Take(500).Select(directory => new DirectoryEntry(directory.Name, directory.FullName)).ToArray(), directories.Length > 500);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new ApiException(403, "The desktop account cannot browse this folder."); }
    }
}
