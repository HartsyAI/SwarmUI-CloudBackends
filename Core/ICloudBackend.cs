using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Common face of every cloud backend this extension manages, whichever of its two shapes it takes
/// (<see cref="CloudBackendBase"/> for serverless workers, <see cref="CloudInstanceBackendBase"/> for
/// rented instances). WebAPI routes that span "all cloud backends" query this one interface, so neither
/// hierarchy can be silently missed.
/// </summary>
public interface ICloudBackend
{
    /// <summary>Human-readable provider name, or null before Init has built the provider.</summary>
    string CloudProviderName { get; }

    /// <summary>User ID of the user this backend belongs to. Every cloud backend is per-user: it runs on its owner's API key, and refuses other users' generations.</summary>
    string OwnerUserId { get; }

    /// <summary>True if this backend's provider was built with the given API key. Used to detect key rotation, never to reveal the key.</summary>
    bool IsUsingApiKey(string apiKey);

    /// <summary>Throw <see cref="SwarmUI.Utils.SwarmReadableErrorException"/> if the session user lacks permission for this backend.</summary>
    void CheckPermission(Session session);

    /// <summary>Network-facing status summary of this backend for the CloudGetStatus route.</summary>
    JObject GetStatusNet();
}
