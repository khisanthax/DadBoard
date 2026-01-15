namespace DadBoard.Spine.Shared;

public sealed class UpdateState
{
    public string ManifestUrl { get; set; } = "";
    public int ForceCheckToken { get; set; }
    public string LastCheckedUtc { get; set; } = "";
    public string LastResult { get; set; } = "";
    public string LastError { get; set; } = "";
    public string LastErrorCode { get; set; } = "";
    public string LastVersionBefore { get; set; } = "";
    public string LastVersionAfter { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public int ConsecutiveFailures { get; set; }
    public bool UpdatesDisabled { get; set; }
    public string DisabledUntilUtc { get; set; } = "";
    public string LastResetUtc { get; set; } = "";
    public string LastResetBy { get; set; } = "";
    public string FallbackManifestUrl { get; set; } = "";
}
