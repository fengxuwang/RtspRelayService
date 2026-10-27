using System.Diagnostics;
using System.Text.Json;
using ActivityRecordingService.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ActivityRecordingService.Tests.Configuration;

public sealed class ActivityRecordingOptionsValidatorTests : IDisposable
{
    private readonly DirectoryInfo _baseDirectory = Directory.CreateTempSubdirectory("activity-recording-options-");

    public ActivityRecordingOptionsValidatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_baseDirectory.FullName, "ffmpeg"));
        Directory.CreateDirectory(Path.Combine(_baseDirectory.FullName, "models"));
        File.WriteAllText(Path.Combine(_baseDirectory.FullName, "ffmpeg/ffmpeg.exe"), string.Empty);
        File.WriteAllText(Path.Combine(_baseDirectory.FullName, "models/yolo11n.onnx"), string.Empty);
    }

    [Fact]
    public void Validate_MissingOrDisabledOptions_ReturnsNoCameras()
    {
        var result = ActivityRecordingOptionsValidator.Validate(new ActivityRecordingOptions(), BaseDirectory, null, null);

        result.Cameras.Should().BeEmpty();
    }

    [Fact]
    public void Validate_DisabledOptionsWithNullFfmpegPath_ReturnsNoCameras()
    {
        var options = new ActivityRecordingOptions { FfmpegPath = null! };

        var result = ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        result.Cameras.Should().BeEmpty();
    }

    [Fact]
    public void Validate_EnabledWithNullFfmpegPath_ThrowsOptionsValidation()
    {
        var options = ValidOptions();
        options.FfmpegPath = null!;

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        validate.Should().Throw<OptionsValidationException>().WithMessage("*FfmpegPath is required.*");
    }

    [Fact]
    public void Validate_EnabledWithNullBaseDirectory_ThrowsOptionsValidation()
    {
        var options = ValidOptions();

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, null!, null, null);

        validate.Should().Throw<OptionsValidationException>().WithMessage("*Base directory is required.*");
    }

    [Fact]
    public void Validate_EnabledWithoutCameras_Throws()
    {
        var options = ValidOptions();

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        validate.Should().Throw<OptionsValidationException>().WithMessage("*No valid enabled cameras remain.*");
    }

    [Fact]
    public void Validate_SkipsDisabledOrInvalidCamera()
    {
        var options = ValidOptions();
        options.Cameras =
        [
            ValidCamera("disabled", enabled: false),
            CreateCamera("invalid-url", camera => camera.DetectionUrl = "http://camera.example/main"),
            CreateCamera("invalid-roi", camera => camera.Roi = new NormalizedRect(0.75, 0, 0.5, 0.5)),
            ValidCamera("valid")
        ];

        var result = ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        result.Cameras.Should().ContainSingle().Which.Name.Should().Be("valid");
        result.Errors.Should().Contain(error => error.Contains("invalid-url", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.Contains("invalid-roi", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SkipsEnabledCameraWithNullName()
    {
        var options = ValidOptions();
        options.Cameras =
        [
            ValidCamera("valid"),
            ValidCamera(null!)
        ];

        var result = ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        result.Cameras.Should().ContainSingle().Which.Name.Should().Be("valid");
        result.Errors.Should().Contain(error => error.Contains("Name is required.", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsDuplicateNames()
    {
        var options = ValidOptions();
        options.Cameras = [ValidCamera("Camera-01"), ValidCamera("camera-01")];

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        validate.Should().Throw<OptionsValidationException>().WithMessage("*camera-01*");
    }

    [Fact]
    public void Validate_RejectsPublicApiHost()
    {
        var options = ValidOptions();
        options.Api.Host = "203.0.113.10";
        options.Cameras = [ValidCamera("camera-01")];

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        validate.Should().Throw<OptionsValidationException>().WithMessage("*private*");
    }

    [Fact]
    public void Validate_RejectsShortSigningKeys()
    {
        var options = ValidOptions();
        options.Api.PlaybackUrlSigningKey = "short-signing-key";
        options.Cameras = [ValidCamera("camera-01")];

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        validate.Should().Throw<OptionsValidationException>().WithMessage("*at least 32 characters*");
    }

    [Fact]
    public void Validate_RejectsQuotaExceedingDrive()
    {
        var options = ValidOptions();
        options.Cameras = [ValidCamera("camera-01")];
        const long totalBytes = 579_820_582_399;

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, totalBytes, totalBytes);

        validate.Should().Throw<OptionsValidationException>().WithMessage("*less than recording drive capacity*");
    }

    [Fact]
    public void Validate_AllowsStartupWhenAvailableSpaceEqualsMinimumFreeWaterMark()
    {
        var options = ValidOptions();
        options.Cameras = [ValidCamera("camera-01")];
        const long totalBytes = 1_000_000_000_000;

        var result = ActivityRecordingOptionsValidator.Validate(
            options,
            BaseDirectory,
            totalBytes,
            options.Recording.MinFreeBytes);

        result.Cameras.Should().ContainSingle();
    }

    [Fact]
    public void Validate_EnabledWithExplicitNestedJsonNullsThrowsOptionsValidation()
    {
        var options = ValidOptions();
        options.Detection = null!;
        options.Recording = null!;
        options.Api = null!;
        options.Cameras = null!;

        var validate = () => ActivityRecordingOptionsValidator.Validate(
            options,
            BaseDirectory,
            null,
            null);

        validate.Should().Throw<OptionsValidationException>()
            .WithMessage("*Detection section is required.*Recording section is required.*Api section is required.*Cameras section is required.*");
    }

    [Fact]
    public void Validate_RequiresModelForEnabledCameras()
    {
        var options = ValidOptions();
        options.Detection.ModelPath = "models/missing.onnx";
        options.Cameras = [ValidCamera("camera-01")];

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        validate.Should().Throw<OptionsValidationException>().WithMessage("*model*");
    }

    [Fact]
    public void Validate_AcceptsChineseCameraDisplayName()
    {
        var options = ValidOptions();
        options.Cameras = [CreateCamera("camera-01", camera => camera.DisplayName = "前门")];

        var result = ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        result.Errors.Should().BeEmpty();
        result.Cameras.Should().ContainSingle().Which.Name.Should().Be("camera-01");
    }

    [Fact]
    public void Validate_RejectsBlankCameraDisplayName()
    {
        var options = ValidOptions();
        options.Cameras = [CreateCamera("camera-01", camera => camera.DisplayName = "   ")];

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        validate.Should().Throw<OptionsValidationException>()
            .WithMessage("*camera-01: DisplayName must not be blank when present.*");
    }

    [Fact]
    public void Validate_RejectsOverlongCameraDisplayName()
    {
        var options = ValidOptions();
        options.Cameras =
            [CreateCamera("camera-01", camera => camera.DisplayName = new string('名', 65))];

        var validate = () => ActivityRecordingOptionsValidator.Validate(options, BaseDirectory, null, null);

        validate.Should().Throw<OptionsValidationException>()
            .WithMessage("*camera-01: DisplayName must be at most 64 characters.*");
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
        Lines(checkIgnore.Output).Should().Contain(expectedPaths);

        var listedFiles = RunGit(repository, ["ls-files", "--", "src/*/appsettings.json"]);
        listedFiles.ExitCode.Should().Be(0);
        listedFiles.Output.Should().BeEmpty();

        File.Exists(Path.Combine(repository.FullName, "src/ActivityRecordingService/appsettings.example.json"))
            .Should().BeTrue();
    }

    [Fact]
    public void FindRepositoryRoot_ReturnsCloneRootWhenGitIsDirectory()
    {
        var cloneRoot = _baseDirectory.CreateSubdirectory("ordinary-clone");
        Directory.CreateDirectory(Path.Combine(cloneRoot.FullName, ".git"));
        var nestedDirectory = cloneRoot.CreateSubdirectory(Path.Combine("tests", "ActivityRecordingService.Tests"));

        var repository = FindRepositoryRoot(nestedDirectory);

        repository.FullName.Should().Be(cloneRoot.FullName);
    }

    private string BaseDirectory => _baseDirectory.FullName;

    private static ActivityRecordingOptions ValidOptions()
    {
        var options = new ActivityRecordingOptions
        {
            Enabled = true,
            RecordingRoot = Path.Combine(Path.GetTempPath(), "ActivityRecordings"),
            Api =
            {
                Host = "10.8.0.2",
                JwtSigningKey = "dW5pdC10ZXN0LWp3dC1zaWduaW5nLWtleS0wMTIzNDU2Nzg5YWJjZGVm",
                PlaybackUrlSigningKey = "this-is-a-valid-playback-signing-key-0123456789"
            }
        };
        return options;
    }

    private static CameraOptions ValidCamera(string name, bool enabled = true) => new()
    {
        Name = name,
        Enabled = enabled,
        DetectionUrl = "rtsp://camera.example/sub",
        RecordUrl = "rtsp://camera.example/main"
    };

    private static CameraOptions CreateCamera(string name, Action<CameraOptions> configure)
    {
        var camera = ValidCamera(name);
        configure(camera);
        return camera;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        return FindRepositoryRoot(new DirectoryInfo(AppContext.BaseDirectory));
    }

    private static DirectoryInfo FindRepositoryRoot(DirectoryInfo startDirectory)
    {
        var directory = startDirectory;
        while (directory is not null && !IsGitMetadata(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test assembly should run below the repository root");
        return directory!;
    }

    private static bool IsGitMetadata(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static GitResult RunGit(DirectoryInfo repository, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repository.FullName);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();
        var output = process!.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new GitResult(process.ExitCode, output + error);
    }

    private static string[] Lines(string value) =>
        value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        _baseDirectory.Delete(recursive: true);
    }

    private sealed record GitResult(int ExitCode, string Output);
}
