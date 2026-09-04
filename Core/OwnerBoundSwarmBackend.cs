using SwarmUI.Backends;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// A <see cref="SwarmSwarmBackend"/> bound to the <see cref="CloudInstanceBackendBase"/> that attached
/// it. Core spawns the generating children of a swarm backend from the same type record as their
/// parent, so every child in the tree inherits this ownership gate. Without it, core's own
/// SwarmSwarmBackend would accept any user's generations on an instance billed to one user's key.
/// </summary>
public class OwnerBoundSwarmBackend : SwarmSwarmBackend
{
    /// <summary>
    /// Walks up to the cloud instance backend this tree hangs off: <see cref="SwarmSwarmBackend.Parent"/>
    /// links a generating child to its control swarm, whose backend-data parent (set in
    /// <see cref="CloudInstanceBackendBase.AttachChildBackend"/>) is the cloud backend. Null if detached.
    /// </summary>
    public CloudInstanceBackendBase FindCloudRoot()
    {
        SwarmSwarmBackend top = this;
        while (top.Parent is not null)
        {
            top = top.Parent;
        }
        return top.BackendData?.AbstractParent?.AbstractBackend as CloudInstanceBackendBase;
    }

    /// <inheritdoc/>
    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        CloudInstanceBackendBase root = FindCloudRoot();
        string requester = input.SourceSession?.User?.UserID;
        if (root is not null && requester is not null)
        {
            if (requester != root.OwnerUserId)
            {
                input.RefusalReasons.Add($"{root.CloudProviderName ?? "Cloud"} instance backend belongs to another user. Your own is created when you start an instance with your own API key set.");
                return false;
            }
            try
            {
                root.CheckPermission(input.SourceSession);
            }
            catch (SwarmReadableErrorException ex)
            {
                input.RefusalReasons.Add(ex.Message);
                return false;
            }
        }
        return base.IsValidForThisBackend(input);
    }
}
