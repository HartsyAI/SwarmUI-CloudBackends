using SwarmUI.Backends;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Attach/detach for the <see cref="OwnerBoundSwarmBackend"/> that a cloud backend hands its remote URL to.
/// Both cloud shapes use this: instances attach for as long as the instance is rented, serverless attaches
/// only while a woken worker is alive. The settings below are load-bearing rather than stylistic, so they
/// live in one place instead of being restated (and drifting) per call site.
/// </summary>
internal static class CloudChildBackend
{
    /// <summary>
    /// Hands <paramref name="address"/> to a swarm backend as a non-real child. That child is a control
    /// instance in its own right: it mirrors the remote's backends, models and features, and spawns its own
    /// children which perform generation. It is an <see cref="OwnerBoundSwarmBackend"/> rather than core's
    /// plain SwarmSwarmBackend so the whole tree refuses generations from anyone but the owner.
    /// </summary>
    /// <param name="owner">The cloud backend the child hangs off.</param>
    /// <param name="address">The worker's SwarmUI (gateway) address.</param>
    /// <param name="title">Display title for the child.</param>
    /// <param name="connectTimeoutSec">Connection budget; the child gets a quarter of it per attempt.</param>
    /// <param name="authorizationHeader">Full <c>Authorization</c> header value (e.g. <c>Bearer ...</c>) the worker's gateway requires, or null.
    /// Core's swarm backend sends it on every HTTP call and WebSocket, and its generating children inherit it.</param>
    public static BackendHandler.BackendData Attach(AbstractT2IBackend owner, string address, string title, int connectTimeoutSec, string authorizationHeader = null)
    {
        SwarmSwarmBackend.SwarmSwarmBackendSettings settings = new()
        {
            Address = address,
            // Keeps re-polling the worker's backend list, and lets a brief network blip recover (IDLE, not ERRORED).
            AllowIdle = true,
            AllowForwarding = false,
            AllowWebsocket = true,
            ConnectionAttemptTimeoutSeconds = Math.Max(30, connectTimeoutSec / 4),
            AuthorizationHeader = authorizationHeader ?? ""
        };
        return owner.Handler.AddNewNonrealBackend(CloudBackendTypes.OwnerBoundSwarm, owner.BackendData, settings, newData =>
        {
            SwarmSwarmBackend swarm = newData.AbstractBackend as SwarmSwarmBackend;
            swarm.IsSpecialControlled = true;
            swarm.CanLoadModels = false;
            swarm.Title = title;
            // Core never assigns the parent of a nonreal backend; OwnerBoundSwarmBackend's ownership walk relies on it.
            newData.AbstractParent = owner.BackendData;
            newData.UpdateLastReleaseTime();
        });
    }

    /// <summary>Removes an attached child and its whole subtree. Safe to call with a child that is already gone.</summary>
    public static async Task DetachAsync(BackendHandler handler, BackendHandler.BackendData child, string providerName)
    {
        if (child is null)
        {
            return;
        }
        try
        {
            await handler.DeleteById(child.ID);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[{providerName}] Removing child backend #{child.ID} failed: {ex.Message}");
        }
    }
}
