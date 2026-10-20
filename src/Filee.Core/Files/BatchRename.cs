// Batch rename for the File utilities page: a preview of new names from a pattern ({name}, {n}, {ext}, {date}) and an
// optional find / replace, applied only when asked, never over an existing file.

using System.Globalization;

namespace Filee.Core.Files;

/// <summary>A planned rename; <see cref="Problem"/> says why it would be skipped.</summary>
public sealed record RenamePlan(string Path, string NewName, string? Problem)
{
    public string OldName => System.IO.Path.GetFileName(Path);
    public bool Changes => Problem is null && !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

public static class BatchRename
{
    /// <summary>
    /// New names in list order. <paramref name="pattern"/>: {name} (without extension), {n} (counter from
    /// <paramref name="start"/>, padded to the width of the last number), {ext}, {date} (last modified, yyyy-MM-dd).
    /// </summary>
    public static IReadOnlyList<RenamePlan> Plan(IReadOnlyList<string> files, string pattern, string find = "", string replace = "", int start = 1)
    {
        var width = (start + files.Count - 1).ToString(CultureInfo.InvariantCulture).Length;
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RenamePlan>();
        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var extension = System.IO.Path.GetExtension(path).TrimStart('.');
            if (find.Length > 0)
                stem = stem.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
            var name = (string.IsNullOrWhiteSpace(pattern) ? "{name}" : pattern)
                .Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
                .Replace("{n}", (start + i).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'), StringComparison.OrdinalIgnoreCase)
                .Replace("{ext}", extension, StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", File.GetLastWriteTime(path).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (extension.Length > 0 && !pattern.Contains("{ext}", StringComparison.OrdinalIgnoreCase))
                name += "." + extension;

            string? problem = null;
            if (name.Length == 0 || name.Trim('.').Length == 0)
                problem = "empty";
            else if (name.IndexOfAny(invalid) >= 0)
                problem = "invalid";
            else if (!taken.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name)))
                problem = "duplicate";
            else if (!string.Equals(name, System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                     && (File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name))
                         && !files.Contains(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name), StringComparer.OrdinalIgnoreCase)))
                problem = "exists";
            result.Add(new RenamePlan(path, name, problem));
        }
        return result;
    }

    /// <summary>
    /// Renames the planned files that change (through temporary names, so names can swap). Returns how many were renamed;
    /// a file that can't be renamed keeps its name.
    /// </summary>
    public static int Apply(IReadOnlyList<RenamePlan> plans)
    {
        var moves = plans.Where(p => p.Changes).ToList();
        var staged = new List<(string Temp, RenamePlan Plan)>();
        foreach (var plan in moves)
        {
            var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(plan.Path)!, $".filee-rename-{Guid.NewGuid():N}");
            try
            {
                File.Move(plan.Path, temp);
                staged.Add((temp, plan));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use: stays as it is
            }
        }
        var renamed = 0;
        foreach (var (temp, plan) in staged)
        {
            var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(plan.Path)!, plan.NewName);
            try
            {
                File.Move(temp, target, overwrite: false);
                renamed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(temp, plan.Path); // back to its own name
            }
        }
        return renamed;
    }
}
