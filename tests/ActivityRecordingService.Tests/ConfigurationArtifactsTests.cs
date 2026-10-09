using System.Diagnostics;
using FluentAssertions;

namespace ActivityRecordingService.Tests;

public sealed class ConfigurationArtifactsTests
{
    [Fact]
    public void Program_UsesWebApplicationCompositionAndRemovesBusyWorker()
    {
        var repository = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            repository.FullName,
            "src",
            "ActivityRecordingService",
            "Program.cs"));

        program.Should().Contain("WebApplication.CreateBuilder");
        program.Should().NotContain("AddHostedService<Worker>");
        program.Should().Contain("AddWindowsService");
        program.Should().Contain("RecordingApi.Map");
    }

    [Fact]
    public void Program_DisabledModeUsesServiceCapableIdleHost()
    {
        var repository = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            repository.FullName,
            "src",
            "ActivityRecordingService",
            "Program.cs"));

        program.Should().Contain("Host.CreateApplicationBuilder(args)");
        program.Should().NotContain("new HostApplicationBuilder(args)");
        CountOccurrences(program, "AddWindowsService").Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void RealAppSettings_AreIgnoredAndAbsentFromIndex()
    {
        var repository = FindRepositoryRoot();
        var expectedPaths = new[]
        {
            "src/RtspRelayService/appsettings.json",
            "src/ActivityRecordingService/appsettings.json"
        };

        var checkIgnore = RunGit(repository, ["check-ignore", "--", .. expectedPaths]);
        checkIgnore.ExitCode.Should().Be(0);
        checkIgnore.Output.Lines().Should().Contain(expectedPaths);

        var listedFiles = RunGit(repository, ["ls-files", "--", "src/*/appsettings.json"]);
        listedFiles.ExitCode.Should().Be(0);
        listedFiles.Output.Lines().Should().BeEmpty();
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".git")) &&
               !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        return directory ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static ProcessResult RunGit(DirectoryInfo repository, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                Arguments = string.Join(' ', arguments.Select(arg => $"\"{arg}\"")),
                WorkingDirectory = repository.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.Trim());
    }

    private static int CountOccurrences(string value, string substring) =>
        value.Split(substring, StringSplitOptions.None).Length - 1;

    private sealed record ProcessResult(int ExitCode, string Output);
}

internal static class TestStringExtensions
{
    public static IReadOnlyList<string> Lines(this string value) =>
        value.Split(new[] { "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
}
