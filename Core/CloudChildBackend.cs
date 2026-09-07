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
    public static BackendHandler.BackendData Attach(AbstractT2IBackend owner, string address, string title, int connectTimeoutSec)
    {
        SwarmSwarmBackend.SwarmSwarmBackendSettings settings = new()
        {
            Address = address,
            // AllowIdle does two things we need: SwarmSwarmBackend only re-polls the remote's backend list
            // (ReviseRemoteDataList) via its idle monitor, so without this a backend added on the remote after
            // attach (or removed and re-added) is never picked up without a manual restart. It also lets a
            // transient connectivity blip recover on its own by going IDLE instead of ERRORED, which matters
            // more here than for a same-machine remote since a cloud proxy URL is more prone to brief hiccups.
            AllowIdle = true,
            AllowForwarding = false,
            AllowWebsocket = true,
            ConnectionAttemptTimeoutSeconds = Math.Max(30, connectTimeoutSec / 4)
        };
        return owner.Handler.AddNewNonrealBackend(CloudBackendTypes.OwnerBoundSwarm, owner.BackendData, settings, newData =>
        {
            SwarmSwarmBackend swarm = newData.AbstractBackend as SwarmSwarmBackend;
            swarm.IsSpecialControlled = true;
            swarm.CanLoadModels = false;
            swarm.Title = title;
            // Core's AddNewNonrealBackend takes a parent argument but never assigns it - set it ourselves,
            // since OwnerBoundSwarmBackend's ownership walk relies on this exact link.
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
        try { await handler.DeleteById(child.ID); }
        catch (Exception ex) { Logs.Debug($"[{providerName}] Removing child backend #{child.ID} failed: {ex.Message}"); }
    }
}
