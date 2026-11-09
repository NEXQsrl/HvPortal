namespace HvPortal.Contracts.HyperV;

/// <summary>Validation of the protocol 11 requests (failover clusters). Same rules on the gateway and on the agent.</summary>
public static partial class OperationValidation
{
    public static string? Validate(ClusterNodeMaintenanceRequest r)
    {
        if (!ClusterNames.IsValidNodeName(r.NodeName)) return "invalid_node_name";
        if (!MaintenanceActions.All.Contains(r.Action, StringComparer.Ordinal)) return "invalid_maintenance_action";
        if (r.Failback && r.Action != MaintenanceActions.Exit) return "invalid_maintenance_action";
        return null;
    }

    public static string? Validate(MoveClusterVmRequest r)
    {
        if (r.VmId == Guid.Empty) return "invalid_vm";
        return ClusterNames.IsValidNodeName(r.NodeName) ? null : "invalid_node_name";
    }
}
