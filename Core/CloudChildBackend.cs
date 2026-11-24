using SwarmUI.Backends;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>Attaches and detaches the <see cref="OwnerBoundSwarmBackend"/> that a cloud backend hands its remote URL to: instances for as long as they are rented, serverless only while a woken worker is alive.
/// The settings are load-bearing rather than stylistic, so they live here instead of at each call site.</summary>
internal static class CloudChildBackend
{
    /// <summary>Hands <paramref name="address"/> to a swarm backend as a non-real child: a control instance that mirrors the remote's backends, models and features and spawns its own generating children.
    /// It is an <see cref="OwnerBoundSwarmBackend"/> rather than core's SwarmSwarmBackend so the whole tree refuses generations from anyone but the owner.
    /// <c>AllowIdle</c> keeps re-polling the worker's backend list and lets a brief network blip recover as IDLE rather than ERRORED.
    /// Core never assigns the parent of a nonreal backend, which the ownership walk relies on, so it is set here.</summary>
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
