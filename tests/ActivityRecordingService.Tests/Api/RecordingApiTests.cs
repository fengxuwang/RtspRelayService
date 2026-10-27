using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Globalization;
using ActivityRecordingService.Api;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Recording;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ActivityRecordingService.Tests.Api;

public sealed class RecordingApiTests : IClassFixture<RecordingApiFactory>
{
    private readonly RecordingApiFactory _factory;

    public RecordingApiTests(RecordingApiFactory factory)
    {
        _factory = factory;
        factory.Reset();
    }

    [Fact]
    public async Task MetadataEndpoints_WithoutJwt_Return401()
    {
        using var client = _factory.CreateClient();
        var eventId = Guid.NewGuid();

        (await client.GetAsync("/recordings/api/events")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync($"/recordings/api/events/{eventId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/recordings/api/files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync($"/recordings/api/events/{eventId}/playback-urls"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Events_WithRawAuthorizationHeader_IsAccepted()
    {
        Seed();
        using var client = _factory.CreateAuthorizedClientWithRawAuthorizationHeader();

        var response = await client.GetAsync("/recordings/api/events?offset=0&limit=20");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("refresh_token")]
    public async Task Events_WithNonAccessTokenJwtType_Return401(string? tokenType)
    {
        Seed();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateTestToken(DateTimeOffset.UtcNow.AddMinutes(15), tokenType));

        var response = await client.GetAsync("/recordings/api/events?offset=0&limit=20");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cameras_WithBearerJwt_ReturnsConfiguredCameras()
    {
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/recordings/api/cameras");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ApiResultForTest<List<CameraResponseForTest>>>())!.Data!;
        var camera = result.Should().ContainSingle().Subject;
        camera.Name.Should().Be("front-door");
        camera.DisplayName.Should().Be("前门");
    }

    [Fact]
    public async Task Events_WithRecordingRows_HidesOngoingEvents()
    {
        _factory.Repository.Events.Add(
            NewEvent(Guid.NewGuid(), "front-door", _factory.Now.AddMinutes(-1)));
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/recordings/api/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<ApiResultForTest<EventPageResponseForTest>>())!.Data!;
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Events_WithBearerJwt_ReturnsRequestedPage()
    {
        var events = new[]
        {
            NewEvent(Guid.NewGuid(), "front-door", _factory.Now.AddMinutes(-30), _factory.Now.AddMinutes(-28)),
            NewEvent(Guid.NewGuid(), "front-door", _factory.Now.AddMinutes(-20), _factory.Now.AddMinutes(-18)),
            NewEvent(Guid.NewGuid(), "yard", _factory.Now.AddMinutes(-10), _factory.Now.AddMinutes(-9))
        };
        foreach (var item in events)
        {
            _factory.Repository.Events.Add(item);
        }

        using var client = _factory.CreateAuthorizedClient();
        var response = await client.GetAsync(
            "/recordings/api/events?camera=front-door&offset=1&limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<ApiResultForTest<EventPageResponseForTest>>())!.Data!;
        page!.Offset.Should().Be(1);
        page.Limit.Should().Be(1);
        page.Items.Should().ContainSingle().Which.EventId.Should().Be(events[0].EventId);
        page.Items.Should().ContainSingle().Which.CameraDisplayName.Should().Be("前门");
    }

    [Fact]
    public async Task Events_WithoutPagingQuery_UsesDefaultPage()
    {
        _factory.Repository.Events.AddRange(Enumerable.Range(0, 21)
            .Select(index => NewEvent(
                Guid.NewGuid(),
                "front-door",
                _factory.Now.AddMinutes(-index),
                _factory.Now.AddMinutes(-index).AddMinutes(1))));
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/recordings/api/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<ApiResultForTest<EventPageResponseForTest>>())!.Data!;
        page!.Offset.Should().Be(0);
        page.Limit.Should().Be(20);
        page.Items.Should().HaveCount(20);
    }

    [Fact]
    public async Task Events_WithDeletedRows_ReturnsFullPageOfActiveEvents()
    {
        var newest = NewEvent(Guid.NewGuid(), "front-door", _factory.Now.AddMinutes(-1), _factory.Now);
        var deleted = NewEvent(Guid.NewGuid(), "front-door", _factory.Now.AddMinutes(-2), _factory.Now.AddMinutes(-1)) with
        {
            Status = EventStatus.Deleted
        };
        var firstOlderActive = NewEvent(Guid.NewGuid(), "front-door", _factory.Now.AddMinutes(-3), _factory.Now.AddMinutes(-2));
        var secondOlderActive = NewEvent(Guid.NewGuid(), "front-door", _factory.Now.AddMinutes(-4), _factory.Now.AddMinutes(-3));
        _factory.Repository.Events.AddRange(
            [newest, deleted, firstOlderActive, secondOlderActive]);
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/recordings/api/events?offset=1&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<ApiResultForTest<EventPageResponseForTest>>())!.Data!;
        page!.Items.Select(item => item.EventId)
            .Should().Equal(firstOlderActive.EventId, secondOlderActive.EventId);
    }

    [Theory]
    [InlineData("offset=-1", "limit=20")]
    [InlineData("offset=0", "limit=0")]
    [InlineData("offset=0", "limit=101")]
    [InlineData("fromUtc=2026-09-29T10:00:00Z", "toUtc=2026-09-29T09:00:00Z")]
    public async Task Events_WithInvalidQuery_Returns400(string first, string second)
    {
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync($"/recordings/api/events?{first}&{second}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EventDetail_ReturnsEventAndAllIndexedFiles()
    {
        var seed = Seed();
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync($"/recordings/api/events/{seed.Event.EventId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ApiResultForTest<EventResponseForTest>>())!.Data!;
        result!.EventId.Should().Be(seed.Event.EventId);
        result.CameraName.Should().Be(seed.Event.CameraName);
        result.CameraDisplayName.Should().Be("前门");
        result.Files.Select(file => file.FileId)
            .Should().ContainInOrder(seed.ReadyFile.FileId, seed.FailedFile.FileId);
    }

    [Fact]
    public async Task EventDetail_SerializesTimestampsInChinaStandardTime()
    {
        var startedUtc = DateTimeOffset.Parse("2026-09-29T01:30:00Z");
        var endedUtc = startedUtc.AddMinutes(10);
        var eventId = Guid.NewGuid();
        _factory.Repository.Events.Add(
            NewEvent(eventId, "front-door", startedUtc, endedUtc));
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync($"/recordings/api/events/{eventId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var startedAt = document.RootElement
            .GetProperty("data")
            .GetProperty("startedAt")
            .GetString()
            .Should().NotBeNull().And.Subject;
        startedAt.Should().Be("2026-09-29 09:30:00");
        var endedAt = document.RootElement
            .GetProperty("data")
            .GetProperty("endedAt")
            .GetString()
            .Should().NotBeNull().And.Subject;
        endedAt.Should().Be("2026-09-29 09:40:00");
    }

    [Fact]
    public async Task Files_WithValidJwt_ReturnsFileCentricPage()
    {
        var seed = Seed();
        _factory.Repository.Files.Add(NewReadyFile(Guid.NewGuid(), seed.OtherEvent.EventId));
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/recordings/api/files?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<ApiResultForTest<FilePageResponseForTest>>())!.Data!;
        page!.Offset.Should().Be(0);
        page.Limit.Should().Be(10);
        var items = page.Items.Should().HaveCount(3).And.Subject.ToList();

        var yardFile = items[0];
        yardFile.EventId.Should().Be(seed.OtherEvent.EventId);
        yardFile.CameraName.Should().Be("yard");
        yardFile.CameraDisplayName.Should().Be("yard");
        yardFile.Status.Should().Be("Ready");
        yardFile.PlaybackUrl.Should().NotBeNull();
        yardFile.PlaybackUrl.Should().StartWith(
            $"/recordings/api/files/{yardFile.FileId}/content?");
        ParseApiTime(yardFile.PlaybackExpiresAt!).Should().Be(_factory.Now.AddMinutes(10));

        var frontDoorFiles = items.Where(item => item.CameraName == "front-door").ToList();
        frontDoorFiles.Should().HaveCount(2);
        frontDoorFiles.Should().OnlyContain(item => item.CameraDisplayName == "前门");
        frontDoorFiles.Select(item => item.Status).Should().ContainInConsecutiveOrder("Ready", "Failed");
        frontDoorFiles[0].Status.Should().Be("Ready");
        frontDoorFiles[0].PlaybackUrl.Should().NotBeNull();
        frontDoorFiles[1].Status.Should().Be("Failed");
        frontDoorFiles[1].PlaybackUrl.Should().BeNull();
        frontDoorFiles[1].PlaybackExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Files_WithCameraFilter_ReturnsOnlyThatCamera()
    {
        var seed = Seed();
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/recordings/api/files?camera=front-door");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<ApiResultForTest<FilePageResponseForTest>>())!.Data!;
        page!.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(item => item.CameraName == "front-door");
    }

    [Fact]
    public async Task EventDetail_WhenMissing_Returns404()
    {
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync($"/recordings/api/events/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EventMetadata_WhenEventIsDeleted_Returns404()
    {
        var seed = Seed();
        _factory.Repository.Events.Remove(seed.Event);
        _factory.Repository.Events.Add(seed.Event with { Status = EventStatus.Deleted });
        using var client = _factory.CreateAuthorizedClient();

        var detail = await client.GetAsync($"/recordings/api/events/{seed.Event.EventId}");
        var playback = await client.GetAsync($"/recordings/api/events/{seed.Event.EventId}/playback-urls");

        detail.StatusCode.Should().Be(HttpStatusCode.NotFound);
        playback.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PlaybackUrls_WithValidJwt_ReturnsOneUrlPerReadyFile()
    {
        var seed = Seed();
        _factory.Repository.Files.Add(NewReadyFile(Guid.NewGuid(), seed.OtherEvent.EventId));
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync($"/recordings/api/events/{seed.Event.EventId}/playback-urls");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var urls = (await response.Content
            .ReadFromJsonAsync<ApiResultForTest<List<PlaybackUrlResponseForTest>>>())!.Data!;
        var url = urls.Should().ContainSingle().Which;
        url.FileId.Should().Be(seed.ReadyFile.FileId);
        url.PartIndex.Should().Be(seed.ReadyFile.PartIndex);
        ParseApiTime(url.ExpiresAt).Should().Be(_factory.Now.AddMinutes(10));
        url.Url.Should().StartWith(
            $"/recordings/api/files/{seed.ReadyFile.FileId}/content?");
        url.Url.Should().Contain("exp=");
        url.Url.Should().Contain("nonce=");
        url.Url.Should().Contain("signature=");
    }

    [Fact]
    public async Task PlaybackUrls_WhenEventMissing_Returns404()
    {
        using var client = _factory.CreateAuthorizedClient();

        var response = await client.GetAsync($"/recordings/api/events/{Guid.NewGuid()}/playback-urls");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FileContent_WithValidSignedUrl_ReturnsMp4WithoutJwt()
    {
        var seed = Seed();
        var url = _factory.Signer.CreateUrl(
            "/recordings",
            seed.ReadyFile.FileId,
            _factory.Now.AddMinutes(10));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("video/mp4");
        response.Headers.AcceptRanges.Should().Contain("bytes");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(RecordingApiFactory.FileBytes);
        response.Content.Headers.ContentDisposition!.FileName.Should().NotContain(_factory.RecordingRoot);
    }

    [Fact]
    public async Task FileContent_WithByteRange_ReturnsExactRange()
    {
        var seed = Seed();
        var url = _factory.Signer.CreateUrl(
            "/recordings",
            seed.ReadyFile.FileId,
            _factory.Now.AddMinutes(10));
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(2, 5);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().Should().Be("bytes 2-5/8");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(FileBytes[2..6]);
    }

    [Fact]
    public async Task FileContent_WithExpiredUrl_Returns401()
    {
        var seed = Seed();
        var url = _factory.Signer.CreateUrl(
            "/recordings",
            seed.ReadyFile.FileId,
            _factory.Now.AddMinutes(10));
        _factory.Time.Set(_factory.Now.AddMinutes(10).AddSeconds(61));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FileContent_WithTamperedOrMissingSignature_Returns401()
    {
        var seed = Seed();
        var url = _factory.Signer.CreateUrl(
            "/recordings",
            seed.ReadyFile.FileId,
            _factory.Now.AddMinutes(10));
        var tampered = url[..^2] + (url[^1] == 'A' ? "B" : "A");
        var withoutSignature = url.Split("&signature=")[0];
        using var client = _factory.CreateClient();

        (await client.GetAsync(tampered)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync(withoutSignature)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/recordings/api/files/" + seed.ReadyFile.FileId + "/content"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FileContent_WithNonNumericExpiry_Returns401()
    {
        var seed = Seed();
        var url = _factory.Signer.CreateUrl(
            "/recordings",
            seed.ReadyFile.FileId,
            _factory.Now.AddMinutes(10));
        var queryIndex = url.IndexOf('?');
        var malformed = url[..(queryIndex + 1)] + url[(queryIndex + 1)..].Split('&')
            .Select(part => part.StartsWith("exp=", StringComparison.Ordinal) ? "exp=not-a-number" : part)
            .Aggregate((left, right) => left + '&' + right);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(malformed);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FileContent_WhenFileIsDeletedOrMissing_Returns404()
    {
        var seed = Seed();
        var missingUrl = _factory.Signer.CreateUrl(
            "/recordings",
            Guid.NewGuid(),
            _factory.Now.AddMinutes(10));
        var deleted = NewReadyFile(Guid.NewGuid(), seed.Event.EventId);
        deleted = deleted with { Status = RecordingFileStatus.Deleted };
        _factory.Repository.Files.Add(deleted);
        var deletedUrl = _factory.Signer.CreateUrl(
            "/recordings",
            deleted.FileId,
            _factory.Now.AddMinutes(10));
        using var client = _factory.CreateClient();

        (await client.GetAsync(missingUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync(deletedUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("../outside-secret.mp4")]
    [InlineData("..\\outside-secret.mp4")]
    public async Task FileContent_WhenRepositoryPathEscapesRoot_Returns404WithoutPathDisclosure(string relativePath)
    {
        var seed = Seed();
        var escaping = seed.ReadyFile with { FileId = Guid.NewGuid(), RelativePath = relativePath };
        _factory.Repository.Files.Add(escaping);
        var url = _factory.Signer.CreateUrl(
            "/recordings",
            escaping.FileId,
            _factory.Now.AddMinutes(10));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain("outside-secret");
        body.Should().NotContain(_factory.RecordingRoot);
    }

    [Fact]
    public async Task FileContent_WhenPhysicalFileIsMissing_Returns404()
    {
        var seed = Seed();
        var missing = seed.ReadyFile with { FileId = Guid.NewGuid(), RelativePath = "events/missing.mp4" };
        _factory.Repository.Files.Add(missing);
        var url = _factory.Signer.CreateUrl(
            "/recordings",
            missing.FileId,
            _factory.Now.AddMinutes(10));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FileContent_WhenRepositoryPathIsSymlinkOutsideRoot_Returns404()
    {
        if (OperatingSystem.IsWindows())
            return;

        var seed = Seed();
        var linkPath = Path.Combine(_factory.RecordingRoot, "events", "linked-outside.mp4");
        var link = File.CreateSymbolicLink(linkPath, _factory.OutsideFilePath);
        var linkedFile = seed.ReadyFile with
        {
            FileId = Guid.NewGuid(),
            RelativePath = "events/linked-outside.mp4"
        };
        _factory.Repository.Files.Add(linkedFile);
        var url = _factory.Signer.CreateUrl(
            "/recordings",
            linkedFile.FileId,
            _factory.Now.AddMinutes(10));
        using var client = _factory.CreateClient();

        try
        {
            var response = await client.GetAsync(url);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            link.Delete();
        }
    }

    [Fact]
    public async Task Health_WithoutAuthentication_ReturnsOnlyAggregateFields()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/recordings/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        foreach (var property in root.EnumerateObject())
        {
            property.Name.Should().BeOneOf(
                "status",
                "serviceState",
                "cameraState",
                "recorderState",
            "diskAvailableBytes",
            "databaseReachable",
            "newEventsAllowed",
            "startupState",
            "cleanupState",
            "blockingReason");
        }

        root.GetProperty("status").GetString().Should().Be("Degraded");
        root.GetProperty("serviceState").GetString().Should().Be("Running");
        root.GetProperty("cameraState").GetString().Should().Be("Partial");
        root.GetProperty("recorderState").GetString().Should().Be("Recording");
        root.GetProperty("diskAvailableBytes").GetInt64().Should().Be(123_456);
        root.GetProperty("databaseReachable").GetBoolean().Should().BeTrue();
        root.GetProperty("newEventsAllowed").GetBoolean().Should().BeFalse();
        root.GetProperty("startupState").GetString().Should().Be("Failed");
        root.GetProperty("cleanupState").GetString().Should().Be("Completed");
        root.GetProperty("blockingReason").GetString().Should().Be("Minimum free disk space reached.");
    }

    private static byte[] FileBytes => RecordingApiFactory.FileBytes;

    private SeedData Seed()
    {
        var eventId = Guid.NewGuid();
        var otherEventId = Guid.NewGuid();
        var @event = NewEvent(eventId, "front-door", _factory.Now.AddMinutes(-30), endedUtc: _factory.Now.AddMinutes(-20));
        var otherEvent = NewEvent(otherEventId, "yard", _factory.Now.AddMinutes(-10));
        _factory.Repository.Events.AddRange([@event, otherEvent]);

        var readyFile = NewReadyFile(Guid.NewGuid(), eventId);
        var failedFile = NewReadyFile(Guid.NewGuid(), eventId) with
        {
            Status = RecordingFileStatus.Failed
        };
        _factory.Repository.Files.AddRange([readyFile, failedFile]);

        return new SeedData(@event, otherEvent, readyFile, failedFile);
    }

    private EventRecord NewEvent(
        Guid eventId,
        string cameraName,
        DateTimeOffset startedUtc,
        DateTimeOffset? endedUtc = null) =>
        new(
            eventId,
            cameraName,
            startedUtc,
            endedUtc,
            endedUtc is null ? EventStatus.Recording : EventStatus.Completed,
            FileBytes.Length,
            1);

    private RecordingFileRecord NewReadyFile(Guid fileId, Guid eventId) =>
        new(
            fileId,
            eventId,
            0,
            "events/part-000.mp4",
            FileBytes.Length,
            RecordingFileStatus.Ready);

    private sealed record SeedData(
        EventRecord Event,
        EventRecord OtherEvent,
        RecordingFileRecord ReadyFile,
        RecordingFileRecord FailedFile);

    private sealed record ApiResultForTest<T>(int Code, T Data);

    private sealed record CameraResponseForTest(string Name, string DisplayName);

    private sealed record EventPageResponseForTest(
        IReadOnlyList<EventResponseForTest> Items,
        int Offset,
        int Limit);

    private sealed record EventResponseForTest(
        Guid EventId,
        string CameraName,
        string CameraDisplayName,
        string StartedAt,
        string? EndedAt,
        string Status,
        long FileBytes,
        int FileCount,
        IReadOnlyList<RecordingFileResponseForTest> Files);

    private sealed record FilePageResponseForTest(
        IReadOnlyList<FileListItemResponseForTest> Items,
        int Offset,
        int Limit);

    private sealed record FileListItemResponseForTest(
        Guid FileId,
        Guid EventId,
        string CameraName,
        string CameraDisplayName,
        int PartIndex,
        long Bytes,
        string Status,
        string StartedAt,
        string? EndedAt,
        string? PlaybackUrl,
        string? PlaybackExpiresAt);

    private sealed record RecordingFileResponseForTest(
        Guid FileId,
        Guid EventId,
        int PartIndex,
        long Bytes,
        string Status);

    private sealed record PlaybackUrlResponseForTest(
        Guid FileId,
        int PartIndex,
        string Url,
        string ExpiresAt);

    private static DateTimeOffset ParseApiTime(string value) =>
        new(
            DateTime.ParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture),
            ChinaStandardTimeJsonConverter.Offset);
}

public sealed class RecordingApiFactory : WebApplicationFactory<Program>
{
    public static readonly byte[] FileBytes = "01234567"u8.ToArray();
    private static readonly DateTimeOffset InitialNow =
        DateTimeOffset.Parse("2026-09-29T10:00:00Z");

    private readonly string _outsideFilePath;
    private readonly string _recordingRoot;
    private WebApplication? _apiApplication;

    public RecordingApiFactory()
    {
        Time = new MutableTimeProvider(InitialNow);
        _recordingRoot = Path.Combine(
            Path.GetTempPath(),
            "activity-recording-api-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_recordingRoot, "events"));
        File.WriteAllBytes(Path.Combine(_recordingRoot, "events", "part-000.mp4"), FileBytes);
        _outsideFilePath = Path.Combine(
            Path.GetTempPath(),
            "activity-recording-api-outside-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(_outsideFilePath, FileBytes);

        Api = new ApiOptions
        {
            JwtSigningKey = "dW5pdC10ZXN0LWp3dC1zaWduaW5nLWtleS0wMTIzNDU2Nzg5YWJjZGVm",
            PlaybackUrlSigningKey = "unit-test-playback-signing-key-0123456789abcdef",
            PlaybackUrlLifetimeMinutes = 10,
            PublicBasePath = "/recordings"
        };
        Options = new ActivityRecordingOptions
        {
            RecordingRoot = _recordingRoot,
            Api = Api
        };
        Options.Cameras.Add(new CameraOptions
        {
            Name = "front-door",
            DisplayName = "前门"
        });
        Repository = new InMemoryEventRepository();
        Signer = new PlaybackUrlSigner(Time, Api);
        Health = new HealthResponse(
            "Degraded",
            "Running",
            "Partial",
            "Recording",
            123_456,
            DatabaseReachable: true,
            NewEventsAllowed: false,
            StartupState: "Failed",
            CleanupState: "Completed",
            BlockingReason: "Minimum free disk space reached.");
    }

    public MutableTimeProvider Time { get; }

    public ApiOptions Api { get; }

    public ActivityRecordingOptions Options { get; }

    public InMemoryEventRepository Repository { get; }

    public PlaybackUrlSigner Signer { get; }

    public HealthResponse Health { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public string RecordingRoot => _recordingRoot;

    public string OutsideFilePath => _outsideFilePath;

    public void Reset()
    {
        Repository.Events.Clear();
        Repository.Files.Clear();
        Time.Set(InitialNow);
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateTestToken(DateTimeOffset.UtcNow.AddMinutes(15)));
        return client;
    }

    public HttpClient CreateAuthorizedClientWithRawAuthorizationHeader()
    {
        var client = CreateClient();
        // 51banshou clients send the raw token without a "Bearer " prefix.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", CreateTestToken(DateTimeOffset.UtcNow.AddMinutes(15)));
        return client;
    }

    public string CreateTestToken(
        DateTimeOffset expiresAtUtc,
        string? tokenType = Program.OAuthAccessTokenType)
    {
        // The JWT bearer handler validates exp against the real system clock
        // (not MutableTimeProvider), so stamp the token with real time.
        var nowUtc = DateTimeOffset.UtcNow;
        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Convert.FromBase64String(Api.JwtSigningKey)),
            SecurityAlgorithms.HmacSha256);
        var header = new JwtHeader(signingCredentials);
        if (tokenType is not null)
        {
            // Mirrors the 51banshou-oauth issuer: jjwt 0.7.0 Header.JWT_TYPE
            // maps to the "JWT" header param.
            header[Program.JjwtTokenTypeHeader] = tokenType;
        }

        var payload = new JwtPayload(
            null,
            null,
            [new Claim("sub", "test-client")],
            nowUtc.UtcDateTime,
            expiresAtUtc.UtcDateTime,
            nowUtc.UtcDateTime);
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(new JwtSecurityToken(header, payload));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddRouting();
            services.AddRecordingApiJsonOptions();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton(Signer);
            services.AddSingleton<IEventRepository>(Repository);
            services.AddSingleton(Health);
            services.AddSingleton<IRecordingHealthMonitor>(new FixedHealthMonitor(Health));
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));

            services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(ConfigureTestJwtBearer);
            services.AddAuthorization();
        });
        var apiApplicationBuilder = WebApplication.CreateBuilder();
        apiApplicationBuilder.Services.AddSingleton<TimeProvider>(Time);
        apiApplicationBuilder.Services.AddSingleton(Signer);
        apiApplicationBuilder.Services.AddSingleton<IEventRepository>(Repository);
        apiApplicationBuilder.Services.AddSingleton(Health);
        apiApplicationBuilder.Services.AddSingleton<IRecordingHealthMonitor>(new FixedHealthMonitor(Health));
        apiApplicationBuilder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        apiApplicationBuilder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(ConfigureTestJwtBearer);
        apiApplicationBuilder.Services.AddAuthorization();
        var apiApplication = apiApplicationBuilder.Build();
        RecordingApi.Map(apiApplication);
        _apiApplication = apiApplication;
        builder.Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                foreach (var dataSource in ((IEndpointRouteBuilder)apiApplication).DataSources)
                {
                    endpoints.DataSources.Add(dataSource);
                }
            });
        });
    }

    private void ConfigureTestJwtBearer(JwtBearerOptions options)
    {
        // Mirrors Program.cs: tokens come from the 51banshou-oauth issuer.
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Convert.FromBase64String(Api.JwtSigningKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                if (authorization.Length > 0 &&
                    !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = authorization;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var tokenType = Program.GetJjwtTokenType(context.SecurityToken);
                if (!string.Equals(tokenType, Program.OAuthAccessTokenType, StringComparison.OrdinalIgnoreCase))
                {
                    context.Fail("Token is not an OAuth access token.");
                }

                return Task.CompletedTask;
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        try
        {
            if (_apiApplication is not null)
            {
                _apiApplication.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Directory.Delete(_recordingRoot, recursive: true);
            File.Delete(_outsideFilePath);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (FileNotFoundException)
        {
        }
    }

    private sealed class FixedHealthMonitor(HealthResponse health) : IRecordingHealthMonitor
    {
        public Task<HealthResponse> GetHealthResponseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(health);
    }
}

public sealed class MutableTimeProvider(DateTimeOffset initialNow) : TimeProvider
{
    private DateTimeOffset _now = initialNow;

    public void Set(DateTimeOffset value) => _now = value;

    public override DateTimeOffset GetUtcNow() => _now;
}

public sealed class InMemoryEventRepository : IEventRepository
{
    public List<EventRecord> Events { get; } = [];

    public List<RecordingFileRecord> Files { get; } = [];

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpsertCameraAsync(
        string cameraName,
        bool enabled,
        int? retentionDays,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<Guid> CreateEventAsync(
        string cameraName,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());

    public Task CompleteEventAsync(
        Guid eventId,
        DateTimeOffset endedUtc,
        EventStatus status,
        CancellationToken cancellationToken,
        EventEndReason? endReason = null) => Task.CompletedTask;

    public Task AddFileAsync(RecordingFileRecord file, CancellationToken cancellationToken)
    {
        Files.Add(file);
        return Task.CompletedTask;
    }

    public Task SetFileStatusAsync(
        Guid fileId,
        RecordingFileStatus status,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetFileReadyAsync(
        Guid fileId,
        long bytes,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<EventRecord?> GetEventAsync(Guid eventId, CancellationToken cancellationToken) =>
        Task.FromResult(Events.SingleOrDefault(item => item.EventId == eventId));

    public Task<IReadOnlyList<RecordingFileRecord>> GetFilesAsync(
        Guid eventId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RecordingFileRecord>>(
            Files.Where(item => item.EventId == eventId).OrderBy(item => item.PartIndex).ToList());

    public Task<RecordingFileRecord?> GetReadyFileAsync(Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult(Files.SingleOrDefault(item =>
            item.FileId == fileId && item.Status == RecordingFileStatus.Ready));

    public Task<IReadOnlyList<EventRecord>> ListEventsAsync(
        string? cameraName,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = Events
            .Where(item => cameraName is null || item.CameraName == cameraName)
            .Where(item => fromUtc is null || item.StartedUtc >= fromUtc)
            .Where(item => toUtc is null || item.StartedUtc <= toUtc)
            .OrderByDescending(item => item.StartedUtc)
            .ThenByDescending(item => item.EventId)
            .Skip(offset)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<EventRecord>>(items);
    }

    public Task<IReadOnlyList<EventRecord>> ListEventsByStatusAsync(
        EventStatus status,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EventRecord>>(
            Events.Where(item => item.Status == status).Take(limit).ToList());

    public Task<IReadOnlyList<FileEventItem>> ListFileEventsAsync(
        string? cameraName,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = Files
            .Where(file => file.Status != RecordingFileStatus.Deleted)
            .Join(
                Events.Where(item => item.Status != EventStatus.Deleted),
                file => file.EventId,
                item => item.EventId,
                (file, item) => new FileEventItem(
                    file.FileId,
                    file.EventId,
                    item.CameraName,
                    file.PartIndex,
                    file.Bytes,
                    file.Status,
                    item.StartedUtc,
                    item.EndedUtc,
                    item.Status))
            .Where(entry => cameraName is null || entry.CameraName == cameraName)
            .OrderByDescending(entry => entry.StartedUtc)
            .ThenBy(entry => entry.PartIndex)
            .Skip(offset)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<FileEventItem>>(items);
    }

    public Task<long> GetRecordingBytesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Files.Where(item => item.Status == RecordingFileStatus.Ready)
            .Sum(item => item.Bytes));

    public Task<IReadOnlyList<EventRecord>> ListDeletableEventsAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EventRecord>>([]);

    public Task AddCleanupAuditAsync(
        string reason,
        int eventCount,
        long freedBytes,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        Events.RemoveAll(item => item.EventId == eventId);
        Files.RemoveAll(item => item.EventId == eventId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecordingFileRecord>> ListFilesByStatusAsync(
        RecordingFileStatus status,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RecordingFileRecord>>(
            Files.Where(item => item.Status == status).Take(limit).ToList());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
