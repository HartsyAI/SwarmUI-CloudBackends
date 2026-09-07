using SwarmUI.Backends;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// A <see cref="SwarmSwarmBackend"/> bound to the <see cref="ICloudBackend"/> that attached
/// it. Core spawns the generating children of a swarm backend from the same type record as their
/// parent, so every child in the tree inherits this ownership gate. Without it, core's own
/// SwarmSwarmBackend would accept any user's generations on an instance billed to one user's key.
/// </summary>
public class OwnerBoundSwarmBackend : SwarmSwarmBackend
{
    /// <summary>
    /// Walks up to the cloud backend this tree hangs off: <see cref="SwarmSwarmBackend.Parent"/> links a
    /// generating child to its control swarm, whose backend-data parent (set in
    /// <see cref="CloudChildBackend.Attach"/>) is the cloud backend. Either cloud shape can be the root -
    /// instances attach a child for as long as one is rented, serverless while a worker is awake - so this
    /// returns the shared interface rather than one of them. Null if detached.
    /// </summary>
    public ICloudBackend FindCloudRoot()
    {
        SwarmSwarmBackend top = this;
        while (top.Parent is not null)
        {
            top = top.Parent;
        }
        return top.BackendData?.AbstractParent?.AbstractBackend as ICloudBackend;
    }

    /// <inheritdoc/>
    /// <remarks>Core routes generations straight here, never to the cloud backend that owns the worker, so
    /// this is the only place that can tell it work is happening. Serverless needs that to keep the worker
    /// alive for as long as generations actually last.</remarks>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        await (FindCloudRoot()?.OnChildGenerationStartingAsync() ?? Task.CompletedTask);
        return await base.Generate(user_input);
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        await (FindCloudRoot()?.OnChildGenerationStartingAsync() ?? Task.CompletedTask);
        await base.GenerateLive(user_input, batchId, takeOutput);
    }

    /// <inheritdoc/>
    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        ICloudBackend root = FindCloudRoot();
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
