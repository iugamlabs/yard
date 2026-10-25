using CliWrap;

namespace Yard;

public static class Processes {
    public static async Task<int> Run(
        string executable,
        IEnumerable<string> args,
        string directory,
        CancellationToken token = default,
        IReadOnlyDictionary<string, string?>? env = null,
        string? input = null,
        bool capture = false,
        Action<string>? observe = null,
        bool outputToError = false
    ) {
        var output = new System.Text.StringBuilder();
        using var force = new CancellationTokenSource();
        using var registration = token.Register(() => force.CancelAfter(TimeSpan.FromSeconds(5)));
        var cmd = Cli.Wrap(executable)
            .WithArguments(args)
            .WithWorkingDirectory(directory)
            .WithValidation(CommandResultValidation.None);
        if (env != null)
            cmd = cmd.WithEnvironmentVariables(env);
        cmd = cmd.WithStandardInputPipe(
            input == null ? PipeSource.Null : PipeSource.FromString(input)
        );
        cmd = cmd.WithStandardOutputPipe(
            capture
                ? PipeTarget.ToStringBuilder(output)
                : PipeTarget.ToStream(
                    outputToError ? Console.OpenStandardError() : Console.OpenStandardOutput()
                )
        );
        cmd = cmd.WithStandardErrorPipe(PipeTarget.ToStream(Console.OpenStandardError()));
        if (observe != null)
            cmd = cmd.WithStandardOutputPipe(
                    PipeTarget.Merge(
                        PipeTarget.ToStream(Console.OpenStandardOutput()),
                        PipeTarget.ToDelegate(observe)
                    )
                )
                .WithStandardErrorPipe(
                    PipeTarget.Merge(
                        PipeTarget.ToStream(Console.OpenStandardError()),
                        PipeTarget.ToDelegate(observe)
                    )
                );
        var result = await cmd.ExecuteAsync(force.Token, token);
        if (result.ExitCode != 0)
            throw new YardException(
                $"{executable} exited with code {result.ExitCode}.",
                result.ExitCode
            );
        return result.ExitCode;
    }

    public static async Task<int> Inherit(
        string executable,
        IEnumerable<string> args,
        string directory,
        CancellationToken token,
        IReadOnlyDictionary<string, string?>? environment = null,
        Func<Task>? onCancellation = null,
        TimeSpan? cancellationTimeout = null
    ) {
        var start = new System.Diagnostics.ProcessStartInfo(executable) {
            WorkingDirectory = directory,
            UseShellExecute = false,
        };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        if (environment != null)
            foreach (var pair in environment)
                start.Environment[pair.Key] = pair.Value;
        using var process =
            System.Diagnostics.Process.Start(start)
            ?? throw new YardException("Could not launch the repository's local tool.");
        try {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) {
            if (onCancellation != null)
                await onCancellation();
            try {
                await process
                    .WaitForExitAsync()
                    .WaitAsync(cancellationTimeout ?? TimeSpan.FromSeconds(45));
            }
            catch (TimeoutException) {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
        return process.ExitCode;
    }

    public static async Task<string> Capture(
        string executable,
        IEnumerable<string> args,
        string directory,
        CancellationToken token = default,
        IReadOnlyDictionary<string, string?>? env = null
    ) {
        var output = new System.Text.StringBuilder();
        var cmd = Cli.Wrap(executable)
            .WithArguments(args)
            .WithWorkingDirectory(directory)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
            .WithStandardErrorPipe(PipeTarget.ToStream(Console.OpenStandardError()))
            .WithValidation(CommandResultValidation.None);
        if (env != null)
            cmd = cmd.WithEnvironmentVariables(env);
        var result = await cmd.ExecuteAsync(token);
        if (result.ExitCode != 0)
            throw new YardException(
                $"{executable} exited with code {result.ExitCode}.",
                result.ExitCode
            );
        return output.ToString();
    }
}
