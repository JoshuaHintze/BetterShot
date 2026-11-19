using System.Runtime.InteropServices;

namespace BetterShot.Recording;

internal enum RecordingProblem
{
    /// <summary>Generic start/encode failure: "Recording failed".</summary>
    Failed,
    /// <summary>The movie could not be finalized or moved into place: "Could not finish the recording".</summary>
    CouldNotFinish,
    Microphone,
    MicrophonePermission,
    Camera,
    CameraPermission,
    SystemAudio,
    Unsupported
}

/// <summary>A recording error with a user-facing message and, for privacy denials, the Settings page that fixes it.</summary>
internal sealed class RecordingException : Exception
{
    internal const string FailedTitle = "Recording failed";
    internal const string CouldNotFinishTitle = "Could not finish the recording";
    internal const string MicrophoneMessage = "The selected microphone could not be opened.";
    internal const string CameraMessage = "The selected camera could not be opened.";
    private const int EAccessDenied = unchecked((int)0x80070005);

    internal RecordingException(RecordingProblem problem, string message, Exception? inner = null) : base(message, inner) => Problem = problem;

    internal RecordingProblem Problem { get; }

    /// <summary>ms-settings URI for privacy denials; null otherwise.</summary>
    internal string? PrivacySettingsUri => Problem switch
    {
        RecordingProblem.MicrophonePermission => "ms-settings:privacy-microphone",
        RecordingProblem.CameraPermission => "ms-settings:privacy-webcam",
        _ => null
    };

    internal string Title => Problem switch
    {
        RecordingProblem.CouldNotFinish => CouldNotFinishTitle,
        RecordingProblem.Microphone or RecordingProblem.MicrophonePermission => "Microphone unavailable",
        RecordingProblem.Camera or RecordingProblem.CameraPermission => "Camera unavailable",
        _ => FailedTitle
    };

    internal static bool IsAccessDenied(Exception error) =>
        error is UnauthorizedAccessException || error.HResult == EAccessDenied ||
        (error is COMException com && com.ErrorCode == EAccessDenied) ||
        (error.InnerException is { } inner && IsAccessDenied(inner));

    internal static RecordingException ForMicrophone(Exception error) => IsAccessDenied(error)
        ? new RecordingException(RecordingProblem.MicrophonePermission, MicrophoneMessage + " Windows privacy settings are blocking microphone access for desktop apps.", error)
        : new RecordingException(RecordingProblem.Microphone, MicrophoneMessage, error);

    internal static RecordingException ForCamera(Exception error) => IsAccessDenied(error)
        ? new RecordingException(RecordingProblem.CameraPermission, CameraMessage + " Windows privacy settings are blocking camera access for desktop apps.", error)
        : new RecordingException(RecordingProblem.Camera, CameraMessage, error);
}
