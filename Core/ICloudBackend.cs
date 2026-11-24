using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>Common face of every cloud backend, serverless (<see cref="CloudBackendBase"/>) or rented instance (<see cref="CloudInstanceBackendBase"/>), so WebAPI routes that span all cloud backends cannot miss either hierarchy.</summary>
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

    /// <summary>Called by an attached <see cref="OwnerBoundSwarmBackend"/> just before it generates, since core routes generations to the child and this backend never sees them.
    /// Serverless keeps the worker alive for as long as work lasts; instances bill by the hour and ignore it. Must never wake or rent anything, as it runs on a path the user did not explicitly ask to spend money on.</summary>
    /// <param name="control">The attached control swarm backend (the top of the subtree) whose worker is about to generate.</param>
    Task OnChildGenerationStartingAsync(SwarmUI.Backends.SwarmSwarmBackend control);
}
