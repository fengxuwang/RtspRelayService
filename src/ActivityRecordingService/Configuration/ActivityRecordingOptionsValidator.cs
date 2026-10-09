using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace ActivityRecordingService.Configuration;

public static class ActivityRecordingOptionsValidator
{
    public static ValidatedActivityRecordingOptions Validate(
        ActivityRecordingOptions options,
        string baseDirectory,
        long? recordingDriveTotalBytes,
        long? recordingDriveAvailableBytes)
    {
        ArgumentNullException.ThrowIfNull(options);

        var nestedSectionErrors = ValidateNestedSections(options);
        Normalize(options);
        var hasBaseDirectory = !string.IsNullOrWhiteSpace(baseDirectory);
        var hasFfmpegPath = !string.IsNullOrWhiteSpace(options.FfmpegPath);
        var ffmpegPath = hasBaseDirectory && hasFfmpegPath
            ? ResolvePath(options.FfmpegPath, baseDirectory)
            : string.Empty;
        if (!options.Enabled)
            return new(ffmpegPath, options, [], []);

        var errors = ValidateGlobal(options, ffmpegPath, baseDirectory, recordingDriveTotalBytes, recordingDriveAvailableBytes);
        errors.AddRange(nestedSectionErrors);
        if (errors.Count > 0)
            throw CreateException(errors);

        var cameraErrors = new List<string>();
        var validCameras = new List<ValidatedCameraOptions>();
        foreach (var camera in options.Cameras.Where(camera => camera.Enabled))
        {
            var cameraError = ValidateCamera(camera);
            if (cameraError.Count == 0)
            {
                validCameras.Add(new(
                    camera.Name,
                    camera.DetectionUrl,
                    camera.RecordUrl,
                    camera.Roi,
                    camera.RetentionDays,
                    camera));
            }
            else
            {
                cameraErrors.Add($"{camera.Name}: {string.Join(" ", cameraError)}");
            }
        }

        if (validCameras.Count == 0)
        {
            errors.AddRange(cameraErrors);
            errors.Add("No valid enabled cameras remain.");
            throw CreateException(errors);
        }

        return new(ffmpegPath, options, validCameras, cameraErrors);
    }

    private static List<string> ValidateGlobal(
        ActivityRecordingOptions options,
        string ffmpegPath,
        string baseDirectory,
        long? recordingDriveTotalBytes,
        long? recordingDriveAvailableBytes)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(baseDirectory))
            errors.Add("Base directory is required.");
        if (string.IsNullOrWhiteSpace(options.FfmpegPath))
            errors.Add("FfmpegPath is required.");
        else if (!File.Exists(ffmpegPath))
            errors.Add($"FFmpeg executable was not found: {ffmpegPath}");

        if (string.IsNullOrWhiteSpace(options.RecordingRoot) || !Path.IsPathRooted(options.RecordingRoot))
            errors.Add("RecordingRoot must be an absolute local path or UNC path.");
        if (options.InitialRestartDelaySeconds <= 0)
            errors.Add("InitialRestartDelaySeconds must be positive.");
        if (options.MaxRestartDelaySeconds < options.InitialRestartDelaySeconds)
            errors.Add("MaxRestartDelaySeconds must be at least InitialRestartDelaySeconds.");
        if (options.StableRunSeconds <= 0 || options.ShutdownTimeoutSeconds <= 0)
            errors.Add("StableRunSeconds and ShutdownTimeoutSeconds must be positive.");
        if (options.InputTimeoutSeconds <= 0)
            errors.Add("InputTimeoutSeconds must be positive.");

        ValidateDetection(options.Detection, baseDirectory, errors);
        ValidateRecording(options.Recording, recordingDriveTotalBytes, recordingDriveAvailableBytes, errors);
        ValidateApi(options.Api, errors);
        ValidateDuplicateCameraNames(options.Cameras, errors);
        return errors;
    }

    private static List<string> ValidateNestedSections(ActivityRecordingOptions options)
    {
        var errors = new List<string>();
        if (options.Detection is null)
            errors.Add("Detection section is required.");
        if (options.Recording is null)
            errors.Add("Recording section is required.");
        if (options.Api is null)
            errors.Add("Api section is required.");
        if (options.Cameras is null)
            errors.Add("Cameras section is required.");
        else if (options.Cameras.Any(camera => camera is null))
            errors.Add("Each Cameras entry must be a JSON object.");
        return errors;
    }

    private static void Normalize(ActivityRecordingOptions options)
    {
        options.FfmpegPath ??= string.Empty;
        options.RecordingRoot ??= string.Empty;
        options.Detection ??= new DetectionOptions();
        options.Recording ??= new RecordingOptions();
        options.Api ??= new ApiOptions();
        options.Cameras ??= [];
        for (var index = 0; index < options.Cameras.Count; index++)
        {
            var camera = options.Cameras[index] ?? new CameraOptions();
            camera.Name ??= string.Empty;
            camera.DetectionUrl ??= string.Empty;
            camera.RecordUrl ??= string.Empty;
            options.Cameras[index] = camera;
        }

        options.Detection.ModelPath ??= string.Empty;
        options.Api.Host ??= string.Empty;
        options.Api.PublicBasePath ??= string.Empty;
        options.Api.JwtSigningKey ??= string.Empty;
        options.Api.PlaybackUrlSigningKey ??= string.Empty;
        options.Api.ApiKeys ??= [];
        foreach (var key in options.Api.ApiKeys)
        {
            if (key is null)
                continue;
            key.Name ??= string.Empty;
            key.Salt ??= string.Empty;
            key.Hash ??= string.Empty;
        }
    }

    private static void ValidateDetection(DetectionOptions detection, string baseDirectory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(detection.ModelPath))
        {
            errors.Add("Detection ModelPath is required.");
            return;
        }

        var modelPath = string.IsNullOrWhiteSpace(baseDirectory)
            ? string.Empty
            : ResolvePath(detection.ModelPath, baseDirectory);
        if (!File.Exists(modelPath))
            errors.Add($"Detection model was not found: {modelPath}");
        if (detection.Width <= 0 || detection.Height <= 0)
            errors.Add("Detection Width and Height must be positive.");
        if (detection.FramesPerSecond <= 0)
            errors.Add("Detection FramesPerSecond must be positive.");
        if (detection.ConfidenceThreshold is < 0 or > 1)
            errors.Add("Detection ConfidenceThreshold must be between 0 and 1.");
        if (detection.PersonClassId < 0)
            errors.Add("Detection PersonClassId must not be negative.");
        if (detection.TriggerHitCount <= 0)
            errors.Add("Detection TriggerHitCount must be positive.");
        if (detection.TriggerWindowSeconds <= 0)
            errors.Add("Detection TriggerWindowSeconds must be positive.");
        if (detection.MaxConcurrentInferences <= 0)
            errors.Add("Detection MaxConcurrentInferences must be positive.");
    }

    private static void ValidateRecording(
        RecordingOptions recording,
        long? recordingDriveTotalBytes,
        long? recordingDriveAvailableBytes,
        List<string> errors)
    {
        if (recording.RollingSegmentSeconds <= 0)
            errors.Add("Recording RollingSegmentSeconds must be positive.");
        if (recording.PreEventSeconds < 0)
            errors.Add("Recording PreEventSeconds must not be negative.");
        if (recording.EventCooldownSeconds <= 0)
            errors.Add("Recording EventCooldownSeconds must be positive.");
        if (recording.EventPartDurationMinutes <= 0)
            errors.Add("Recording EventPartDurationMinutes must be positive.");
        if (recording.MaxPartBytes <= 0)
            errors.Add("Recording MaxPartBytes must be positive.");
        if (recording.MaxEventDurationHours <= 0)
            errors.Add("Recording MaxEventDurationHours must be positive.");
        if (recording.RetentionDays <= 0)
            errors.Add("Recording RetentionDays must be positive.");
        if (recording.MaxRecordingBytes <= 0)
            errors.Add("Recording MaxRecordingBytes must be positive.");
        if (recording.MinFreeBytes <= 0)
            errors.Add("Recording MinFreeBytes must be positive.");
        if (recordingDriveTotalBytes is > 0 && recording.MaxRecordingBytes >= recordingDriveTotalBytes)
            errors.Add("Recording MaxRecordingBytes must be less than recording drive capacity.");
        if (recordingDriveTotalBytes is > 0 && recording.MinFreeBytes >= recordingDriveTotalBytes)
            errors.Add("Recording MinFreeBytes must be less than recording drive capacity.");
        if (recordingDriveAvailableBytes is > 0 && recording.MinFreeBytes > recordingDriveAvailableBytes)
            errors.Add("Recording MinFreeBytes must not exceed recording drive available space.");
    }

    private static void ValidateApi(ApiOptions api, List<string> errors)
    {
        if (!IsAllowedApiHost(api.Host))
            errors.Add("Api Host must be a loopback or private network address, not 0.0.0.0.");
        if (api.Port is < 1 or > 65535)
            errors.Add("Api Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(api.PublicBasePath) || !api.PublicBasePath.StartsWith('/'))
            errors.Add("Api PublicBasePath must start with '/'.");
        if (Encoding.UTF8.GetByteCount(api.JwtSigningKey) < 32)
            errors.Add("Api JwtSigningKey must contain at least 32 characters.");
        if (api.JwtLifetimeMinutes <= 0)
            errors.Add("Api JwtLifetimeMinutes must be positive.");
        if (Encoding.UTF8.GetByteCount(api.PlaybackUrlSigningKey) < 32)
            errors.Add("Api PlaybackUrlSigningKey must contain at least 32 characters.");
        if (api.PlaybackUrlLifetimeMinutes <= 0)
            errors.Add("Api PlaybackUrlLifetimeMinutes must be positive.");
        if (api.ApiKeys.Count == 0)
            errors.Add("At least one ApiKeys entry is required.");

        foreach (var key in api.ApiKeys)
        {
            if (key is null)
            {
                errors.Add("Each ApiKeys entry must be a JSON object.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(key.Name))
                errors.Add("Each API key must have a Name.");
            if (!IsBase64(key.Salt))
                errors.Add($"API key {key.Name} Salt must be a Base64 string.");
            if (!IsBase64(key.Hash))
                errors.Add($"API key {key.Name} Hash must be a Base64 string.");
            if (key.Iterations <= 0)
                errors.Add($"API key {key.Name} Iterations must be positive.");
        }
    }

    private static void ValidateDuplicateCameraNames(List<CameraOptions> cameras, List<string> errors)
    {
        var duplicates = cameras
            .Where(camera => camera.Enabled)
            .GroupBy(camera => camera.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
            errors.Add($"Found duplicate camera names: {string.Join(", ", duplicates)}.");
    }

    private static List<string> ValidateCamera(CameraOptions camera)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(camera.Name))
            errors.Add("Name is required.");
        if (camera.DisplayName is { } displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                errors.Add("DisplayName must not be blank when present.");
            else if (displayName.Length > 64)
                errors.Add("DisplayName must be at most 64 characters.");
        }
        if (!IsRtspUrl(camera.DetectionUrl))
            errors.Add("DetectionUrl must use rtsp or rtsps.");
        if (!IsRtspUrl(camera.RecordUrl))
            errors.Add("RecordUrl must use rtsp or rtsps.");
        if (camera.Roi is { } roi && !IsValidRoi(roi))
            errors.Add("Roi coordinates must be normalized and form a non-empty rectangle.");
        if (camera.RetentionDays is <= 0)
            errors.Add("RetentionDays must be positive.");
        return errors;
    }

    private static bool IsAllowedApiHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || !IPAddress.TryParse(host, out var address))
            return false;
        if (IPAddress.Equals(address, IPAddress.Any) || IPAddress.Equals(address, IPAddress.IPv6Any))
            return false;
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is 10 or 127
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0xfd
                || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
                || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0);
        }

        return false;
    }

    private static bool IsRtspUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Host.Length > 0
        && uri.Scheme is "rtsp" or "rtsps";

    private static bool IsValidRoi(NormalizedRect roi) =>
        roi.Width > 0
        && roi.Height > 0
        && roi.X >= 0
        && roi.Y >= 0
        && roi.X + roi.Width <= 1
        && roi.Y + roi.Height <= 1;

    private static bool IsBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ResolvePath(string path, string baseDirectory) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(baseDirectory, path));

    private static OptionsValidationException CreateException(IEnumerable<string> errors) =>
        new(ActivityRecordingOptions.SectionName, typeof(ActivityRecordingOptions), errors.ToArray());
}
