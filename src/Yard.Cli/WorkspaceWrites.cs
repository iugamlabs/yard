using System.Text;

namespace Yard.Cli;

// An add operation prepares every output before touching the workspace.
// Ordinary I/O failures restore file contents, permissions and newly created directories.
internal static class WorkspaceWrites {
    public static void Apply(string root, IReadOnlyDictionary<string, string> files) {
        var backups = files.Keys.ToDictionary(
            path => path,
            path => {
                var full = Path.Combine(root, path);
                return File.Exists(full) ? File.ReadAllBytes(full) : null;
            }
        );
        var modes = files.Keys.ToDictionary(
            path => path,
            path =>
                !OperatingSystem.IsWindows() && File.Exists(Path.Combine(root, path))
                    ? File.GetUnixFileMode(Path.Combine(root, path))
                    : (UnixFileMode?)null
        );
        var written = new List<string>();
        var directories = new List<string>();
        try {
            foreach (var (path, content) in files) {
                var full = Path.Combine(root, path);
                var missing = new Stack<string>();
                var directory = Path.GetDirectoryName(full)!;
                while (!Directory.Exists(directory)) {
                    missing.Push(directory);
                    directory = Path.GetDirectoryName(directory)!;
                }
                while (missing.TryPop(out directory)) {
                    Directory.CreateDirectory(directory);
                    directories.Add(directory);
                }
                written.Add(path);
                File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
            }
        }
        catch (Exception failure) {
            var errors = new List<Exception> { failure };
            foreach (var path in written.AsEnumerable().Reverse())
                try {
                    var full = Path.Combine(root, path);
                    if (backups[path] is { } original) {
                        File.WriteAllBytes(full, original);
                        if (!OperatingSystem.IsWindows() && modes[path] is { } mode)
                            File.SetUnixFileMode(full, mode);
                    } else if (File.Exists(full))
                        File.Delete(full);
                }
                catch (Exception error) {
                    errors.Add(error);
                }
            foreach (var directory in directories.AsEnumerable().Reverse())
                try {
                    Directory.Delete(directory);
                }
                catch (Exception error) {
                    errors.Add(error);
                }
            if (errors.Count > 1)
                throw new AggregateException(
                    "Application creation failed and some files could not be restored.",
                    errors
                );
            throw;
        }
    }
}
