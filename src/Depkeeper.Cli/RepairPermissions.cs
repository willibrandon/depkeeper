using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Depkeeper.Cli;

/// <summary>
/// Adapts scoped repair policy to the stable SDK's generated permission callback API.
/// </summary>
internal static class RepairPermissions
{
    /// <summary>
    /// Installs a permission handler that respects managed approval requirements.
    /// </summary>
    /// <param name="configuration">The SDK session configuration.</param>
    /// <param name="policy">The workspace access policy.</param>
    internal static void Apply(SessionConfig configuration, WorkspacePolicy policy)
    {
        // SDK 1.x's public callback returns this generated RPC type, which retains its preview annotation.
#pragma warning disable GHCP001
        configuration.OnPermissionRequest = (request, _) => Task.FromResult(request.ManagedApprovalRequired is true
            ? PermissionDecision.UserNotAvailable()
            : Allows(request, policy) ? PermissionDecision.ApproveOnce()
                : PermissionDecision.Reject("Only scoped files and the isolated depkeeper_shell tool are allowed."));
#pragma warning restore GHCP001
    }

    /// <summary>
    /// Determines whether a requested tool operation satisfies the repair boundary.
    /// </summary>
    /// <param name="request">The SDK permission request.</param>
    /// <param name="policy">The scoped filesystem policy.</param>
    /// <returns>Whether automatic approval is allowed.</returns>
    internal static bool Allows(PermissionRequest request, WorkspacePolicy policy) => request.ManagedApprovalRequired is not true &&
        request switch
        {
            PermissionRequestRead read => policy.Allows(read.Path, false),
            PermissionRequestWrite write => policy.Allows(write.FileName, true),
            PermissionRequestCustomTool custom => custom.ToolName == "depkeeper_shell",
            _ => false
        };
}
