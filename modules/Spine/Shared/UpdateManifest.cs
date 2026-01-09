using System.Text.Json.Serialization;

namespace DadBoard.Spine.Shared;

public sealed class UpdateManifest
{
    [JsonPropertyName("latest_version")]
    public string LatestVersion { get; set; } = "";

    [JsonPropertyName("package_url")]
    public string PackageUrl { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("force_check_token")]
    public int ForceCheckToken { get; set; }

    [JsonPropertyName("min_supported_version")]
    public string? MinSupportedVersion { get; set; }
}
