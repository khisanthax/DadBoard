using System.Text.Json.Serialization;

namespace DadBoard.Spine.Shared;

public sealed class UpdateConfig
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("manifest_url")]
    public string ManifestUrl { get; set; } = "";

    [JsonPropertyName("update_channel")]
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Nightly;

    [JsonPropertyName("mirror_enabled")]
    public bool MirrorEnabled { get; set; }

    [JsonPropertyName("mirror_poll_minutes")]
    public int MirrorPollMinutes { get; set; } = 10;

    [JsonPropertyName("local_host_port")]
    public int LocalHostPort { get; set; } = 45555;

    [JsonPropertyName("local_host_ip")]
    public string LocalHostIp { get; set; } = "";
}
