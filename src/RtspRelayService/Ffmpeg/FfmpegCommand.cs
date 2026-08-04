namespace RtspRelayService.Ffmpeg;

public sealed record FfmpegCommand(string FileName, IReadOnlyList<string> Arguments);
