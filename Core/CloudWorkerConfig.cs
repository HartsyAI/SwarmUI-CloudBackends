using System.Text;
using Newtonsoft.Json;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>The startup config a created pod or instance hands its Hartsy worker, parsed by <c>swarmui_worker.startup</c> in SwarmUI-Worker-Base.
/// It is sent as one value under <see cref="TransportKey"/>, since the provider's env map is the only startup channel that keeps the token off the container's command line.</summary>
[JsonObject(MemberSerialization.OptIn, ItemNullValueHandling = NullValueHandling.Ignore)]
public class CloudWorkerConfig
{
    /// <summary>Name of the provider field the config is sent in.</summary>
    public const string TransportKey = "SWARMUI_WORKER_CONFIG";

    /// <summary>Bearer token the worker's gateway requires, at least 32 characters. Never logged.</summary>
    [JsonProperty("worker_token")]
    public string WorkerToken;

    /// <summary>Port the worker's gateway listens on, matching the port the provider exposes.</summary>
    [JsonProperty("public_port")]
    public int PublicPort;

    /// <summary>Absolute path where the volume with the models is mounted, or null to let the image look in its usual places.</summary>
    [JsonProperty("volume_path")]
    public string VolumePath;

    /// <summary>Builds a config. Blank text becomes null, which is left out of what is sent.</summary>
    public CloudWorkerConfig(string workerToken, int publicPort, string volumePath)
    {
        WorkerToken = string.IsNullOrWhiteSpace(workerToken) ? null : workerToken.Trim();
        PublicPort = publicPort;
        VolumePath = string.IsNullOrWhiteSpace(volumePath) ? null : volumePath.Trim();
    }

    /// <summary>The value to send: JSON as unpadded base64url, since Vast.ai turns the field into docker arguments, where JSON's quotes and braces may not survive.</summary>
    public string Encode()
    {
        string json = JsonConvert.SerializeObject(this, Formatting.None);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
