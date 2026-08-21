namespace Lodge.Core.Abstractions;

/// <summary>
/// Decides whether a user may run a given runbook. Access is decided purely by group
/// membership: the user's Entra groups must intersect the runbook's allowed groups.
/// Admins bypass the check. The POC reads allowed groups from a mock permissions file
/// (<c>inventory/{kind}/runbook-permissions.yaml</c>) designed to later be
/// the single source consumed by both Lodge and Octopus/Terraform.
/// </summary>
public interface IPermissionResolver
{
    bool CanRun(AuthenticatedUser user, string runbookRef);
}
