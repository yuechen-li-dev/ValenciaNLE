namespace Valencia.Engine;

/// <summary>Optional local MCP file policy. Rejects traversal and reparse-point aliases, including existing parent directories.</summary>
public sealed class WorkspacePolicy
{
    public string Root { get; }
    public WorkspacePolicy(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Workspace root must not be a link.");
    }
    public string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new UnauthorizedAccessException("MCP paths must be workspace-relative.");
        return Check(Path.GetFullPath(relative, Root));
    }
    public string Check(string absolute)
    {
        string full = Path.GetFullPath(absolute);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path escapes the workspace.");
        for (string? item = full; item is not null && item != Root; item = Path.GetDirectoryName(item))
        {
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Links/reparse points are not permitted in MCP paths.");
        }
        return full;
    }
}
