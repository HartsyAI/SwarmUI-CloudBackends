using SwarmUI.Backends;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>A <see cref="SwarmSwarmBackend"/> bound to the <see cref="ICloudBackend"/> that attached it. Core spawns a swarm backend's generating children from the same type record as the parent,
/// so the whole tree inherits this ownership gate; without it, core's SwarmSwarmBackend would accept any user's generations on an instance billed to one user's key.</summary>
public class OwnerBoundSwarmBackend : SwarmSwarmBackend
{
    /// <summary>The cloud backend (serverless or instance) this tree hangs off, through the control swarm's parent. Null if detached.</summary>
    public ICloudBackend FindCloudRoot()
    {
        return TopControl().BackendData?.AbstractParent?.AbstractBackend as ICloudBackend;
    }

    /// <summary>The control swarm backend at the top of this subtree: the one the cloud backend attached.</summary>
    public SwarmSwarmBackend TopControl()
    {
        SwarmSwarmBackend top = this;
        while (top.Parent is not null)
        {
            top = top.Parent;
        }
        return top;
    }

    /// <inheritdoc/>
    /// <remarks>Core routes generations straight here, never to the cloud backend that owns the worker, so this is the only place that can tell it work is happening and keep a serverless worker alive.</remarks>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        await (FindCloudRoot()?.OnChildGenerationStartingAsync(TopControl()) ?? Task.CompletedTask);
        return await base.Generate(user_input);
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        await (FindCloudRoot()?.OnChildGenerationStartingAsync(TopControl()) ?? Task.CompletedTask);
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
                input.RefusalReasons.Add(root is CloudBackendBase
                    ? $"{root.CloudProviderName ?? "Cloud"} worker belongs to another user. Yours start automatically once your own API key is set in User Settings."
                    : $"{root.CloudProviderName ?? "Cloud"} instance belongs to another user. Yours is created when you start an instance with your own API key set.");
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
