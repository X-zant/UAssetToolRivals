#nullable enable
using System;

namespace UAssetTool;

/// <summary>
/// Helpers for the container paths held in an IoStore directory index.
/// </summary>
public static class VfsPath
{
    /// <summary>
    /// Strip the mount prefix from a container path, including a repeated one.
    /// </summary>
    /// <remarks>
    /// Directory index paths are built from the container's mount point, and a container
    /// mounted deeper than the entries it stores ends up carrying that mount twice:
    /// <c>../../../Marvel/</c> + <c>../../../Marvel/Plugins/X</c>. The real path is whatever
    /// follows the last <c>..</c> segment, so everything up to it is dropped. Collapsing the
    /// <c>..</c> segments arithmetically instead would fold the duplicated head into the
    /// result (<c>Marvel/Marvel/Content/...</c>) rather than removing it.
    /// </remarks>
    public static string StripMountPrefix(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        string p = path.Replace('\\', '/');

        int cut = 0;
        for (int i = 0; i + 2 < p.Length; i++)
        {
            // A ".." that is a whole segment, not the head of a name like "..foo".
            if (p[i] == '.' && p[i + 1] == '.' && p[i + 2] == '/' && (i == 0 || p[i - 1] == '/'))
                cut = i + 3;
        }

        return p[cut..].TrimStart('/');
    }
}
