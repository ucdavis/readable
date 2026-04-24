namespace opendataloader.api;

public sealed record RuntimeDependencyStatus(
    bool Ok,
    string? CommandPath,
    string? JavaPath,
    string? PythonPath,
    string Message);

public interface IRuntimeDependencyProbe
{
    RuntimeDependencyStatus Probe(OpenDataLoaderOptions options);
}

public sealed class RuntimeDependencyProbe : IRuntimeDependencyProbe
{
    public RuntimeDependencyStatus Probe(OpenDataLoaderOptions options)
    {
        var commandPath = FindOnPath(options.CommandPath);
        var javaPath = FindOnPath("java");
        var pythonPath = FindOnPath("python3") ?? FindOnPath("python");

        var missing = new List<string>();
        if (commandPath is null)
        {
            missing.Add(options.CommandPath);
        }

        if (javaPath is null)
        {
            missing.Add("java");
        }

        if (pythonPath is null)
        {
            missing.Add("python3");
        }

        return missing.Count == 0
            ? new RuntimeDependencyStatus(true, commandPath, javaPath, pythonPath, "ok")
            : new RuntimeDependencyStatus(
                false,
                commandPath,
                javaPath,
                pythonPath,
                $"Missing required runtime dependencies: {string.Join(", ", missing)}");
    }

    public static string? FindOnPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command) && File.Exists(command))
        {
            return command;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

