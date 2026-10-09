using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Hosting;

namespace RtspRelayService.Tests.Deployment;

public sealed class DeploymentArtifactsTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProjectPublishesSelfContainedSingleFileWindowsExecutableWithSettings()
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot, "src", "RtspRelayService", "RtspRelayService.csproj"));
        var properties = project
            .Descendants()
            .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
            .ToDictionary(element => element.Name.LocalName, element => element.Value);

        properties["PublishSingleFile"].Should().Be("true");
        properties["SelfContained"].Should().Be("true");
        properties["RuntimeIdentifier"].Should().Be("win-x64");

        var appSettings = project
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Content"
                && (string?)element.Attribute("Update") == "appsettings.json");

        appSettings.Elements().Single(element => element.Name.LocalName == "CopyToOutputDirectory")
            .Value.Should().Be("PreserveNewest");
        appSettings.Elements().Single(element => element.Name.LocalName == "CopyToPublishDirectory")
            .Value.Should().Be("PreserveNewest");
    }

    [Fact]
    public void WindowsPublishDefaultsDoNotOverrideDebugDevelopmentRuntime()
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot, "src", "RtspRelayService", "RtspRelayService.csproj"));
        var publishPropertyGroup = project
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "PropertyGroup"
                && element.Elements().Any(child => child.Name.LocalName == "PublishSingleFile"));

        ((string?)publishPropertyGroup.Attribute("Condition"))
            .Should().Be("'$(Configuration)' == 'Release'");
    }

    [Fact]
    public void ProjectNeverPublishesLocalDevelopmentSettings()
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot, "src", "RtspRelayService", "RtspRelayService.csproj"));
        var developmentSettings = project
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Content"
                && (string?)element.Attribute("Update") == "appsettings.Development.json");

        developmentSettings.Elements()
            .Single(element => element.Name.LocalName == "CopyToPublishDirectory")
            .Value.Should().Be("Never");
    }

    [Fact]
    public void WindowsHostUsesInstalledServiceName()
    {
        var program = ReadRepositoryFile("src", "RtspRelayService", "Program.cs");

        program.Should().Contain("options.ServiceName = \"RtspRelayService\"");
    }

    [Fact]
    public void ProgramCreatesBootstrapLoggingBeforeHostValidationAndAlwaysFlushes()
    {
        var program = ReadRepositoryFile("src", "RtspRelayService", "Program.cs");
        var bootstrapIndex = program.IndexOf("CreateBootstrapLogger()", StringComparison.Ordinal);
        var hostIndex = program.IndexOf("Host.CreateApplicationBuilder", StringComparison.Ordinal);
        var validationIndex = program.IndexOf("RelayOptionsValidator.Validate", StringComparison.Ordinal);

        bootstrapIndex.Should().BeGreaterThanOrEqualTo(0);
        bootstrapIndex.Should().BeLessThan(hostIndex);
        hostIndex.Should().BeLessThan(validationIndex);
        program.Should().Contain("catch (Exception exception)");
        program.Should().Contain("Log.Fatal(");
        program.Should().Contain("UrlRedactor.RedactText(");
        program.Should().Contain("finally");
        program.Should().Contain("Log.CloseAndFlush()");
        program.Should().NotContain("Log.Fatal(exception");
    }

    [Fact]
    public void ProgramReportsSanitizedFatalFailureWithoutRethrowingOriginalException()
    {
        var program = ReadRepositoryFile("src", "RtspRelayService", "Program.cs");

        program.Should().Contain("Environment.ExitCode = 1;");
        program.Should().NotContain("throw;");
        program.Should().Contain("UrlRedactor.RedactText(exception.Message, configuredUrls)");
        program.IndexOf("Environment.ExitCode = 1;", StringComparison.Ordinal)
            .Should().BeLessThan(
                program.IndexOf("finally", StringComparison.Ordinal));
        program.Should().Contain("Log.CloseAndFlush()");
    }

    [Fact]
    public void InstallScriptQuotesExecutableAndConfiguresAutomaticRecovery()
    {
        var script = ReadRepositoryFile("deploy", "windows", "install-service.ps1");

        script.Should().StartWith("#Requires -RunAsAdministrator");
        script.Should().Contain("Join-Path $InstallDirectory \"RtspRelayService.exe\"");
        script.Should().Contain("$binaryPath = '\"{0}\"' -f $exePath");
        script.Should().Contain("BinaryPathName = $binaryPath");
        script.Should().Contain("StartupType = \"Automatic\"");
        script.Should().Contain("sc.exe failure $serviceName");
        script.Should().Contain("restart/5000/restart/10000/restart/60000");
        script.Should().Contain("sc.exe failureflag $serviceName 1");
        script.Should().Contain("$LASTEXITCODE");
        script.Should().Contain("Start-Service -Name $serviceName");
    }

    [Fact]
    public void InstallScriptValidatesDependenciesAndLocksConfigurationBeforeRegistration()
    {
        var script = ReadRepositoryFile("deploy", "windows", "install-service.ps1");
        var registrationIndex = script.IndexOf("New-Service", StringComparison.Ordinal);

        script.Should().Contain("Join-Path $InstallDirectory \"appsettings.json\"");
        script.Should().Contain("Join-Path $InstallDirectory \"ffmpeg\\ffmpeg.exe\"");
        script.Should().Contain("Test-Path -LiteralPath $configPath -PathType Leaf");
        script.Should().Contain("Test-Path -LiteralPath $ffmpegPath -PathType Leaf");
        script.Should().Contain("System.Security.AccessControl.FileSecurity");
        script.Should().Contain("SetAccessRuleProtection($true, $false)");
        script.Should().Contain("S-1-5-32-544");
        script.Should().Contain("S-1-5-18");
        script.Should().Contain("Set-Acl -LiteralPath $configPath");
        script.IndexOf("Set-Acl -LiteralPath $configPath", StringComparison.Ordinal)
            .Should().BeLessThan(registrationIndex);
    }

    [Fact]
    public void UninstallScriptRemovesOnlyServiceRegistration()
    {
        var script = ReadRepositoryFile("deploy", "windows", "uninstall-service.ps1");

        script.Should().StartWith("#Requires -RunAsAdministrator");
        script.Should().Contain("Stop-Service -Name $serviceName");
        script.Should().Contain("WaitForStatus(\"Stopped\"");
        script.Should().Contain("sc.exe delete $serviceName");
        script.Should().Contain("$LASTEXITCODE");
        script.Should().NotContain("Remove-Item");
        script.Should().NotMatch(@"(?im)^\s*(del|erase|rd|rmdir)\b");
        script.Should().Contain("Configuration and logs were preserved.");
    }

    [Fact]
    public void ReadmeMakesDevelopmentConfigurationAndExternalFfmpegExplicit()
    {
        var readme = ReadRepositoryFile("README.md");

        readme.Should().Contain("DOTNET_ENVIRONMENT=Development");
        readme.Should().Contain("appsettings.Development.json");
        readme.Should().Contain("Relay__FfmpegPath=/opt/homebrew/bin/ffmpeg");
        readme.Should().Contain("--self-contained true");
        readme.Should().Contain("publish/rtsp-relay/windows-x64");
        readme.Should().Contain("ffmpeg/ffmpeg.exe");
        readme.Should().Contain("配置文件和日志会保留");
    }

    [Fact]
    public void ReadmeDocumentsConfigurationAclAcceptance()
    {
        var readme = ReadRepositoryFile("README.md");

        readme.Should().Contain("Get-Acl");
        readme.Should().Contain("BUILTIN\\Administrators");
        readme.Should().Contain("NT AUTHORITY\\SYSTEM");
        readme.Should().Contain("IsInherited");
        readme.Should().Contain("appsettings.json");
    }

    [Fact]
    public void DefaultHostConfigurationLoadsDevelopmentSettingsAfterBaseSettings()
    {
        var contentRoot = Directory.CreateTempSubdirectory("rtsp-relay-host-config-");
        try
        {
            File.WriteAllText(
                Path.Combine(contentRoot.FullName, "appsettings.json"),
                """{"Probe":{"Source":"base"}}""");
            File.WriteAllText(
                Path.Combine(contentRoot.FullName, "appsettings.Development.json"),
                """{"Probe":{"Source":"development"}}""");

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = contentRoot.FullName,
                EnvironmentName = Environments.Development
            });

            builder.Configuration["Probe:Source"].Should().Be("development");
        }
        finally
        {
            contentRoot.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("bin/")]
    [InlineData("obj/")]
    [InlineData("publish/")]
    [InlineData("logs/")]
    [InlineData("appsettings.Development.json")]
    [InlineData("TestResults/")]
    public void GitIgnoreExcludesGeneratedOrLocalOnlyArtifacts(string pattern)
    {
        var ignoreRules = ReadRepositoryFile(".gitignore")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        ignoreRules.Should().Contain(pattern);
    }

    private static string ReadRepositoryFile(params string[] pathParts)
    {
        var path = pathParts.Aggregate(RepositoryRoot, Path.Combine);
        File.Exists(path).Should().BeTrue($"required deployment artifact {path} should exist");
        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RtspRelayService.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
