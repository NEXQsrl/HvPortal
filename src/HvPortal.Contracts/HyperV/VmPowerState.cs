namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Normalized VM power state. Derived from Msvm_ComputerSystem.EnabledState
/// (root\virtualization\v2). See docs/capability-matrix.md for the source mapping.
/// </summary>
public enum VmPowerState
{
    Unknown = 0,
    Running,
    Off,
    Saved,
    Paused,
    Starting,
    Stopping,
    Saving,
    Pausing,
    Resuming,
    Resetting,
    Other,
}

/// <summary>Operations exposed by the agent. This is a closed set: the agent never executes arbitrary commands.</summary>
public enum VmPowerOperation
{
    /// <summary>Power on (from Off) or restore (from Saved). RequestStateChange(2).</summary>
    Start,
    /// <summary>Guest OS shutdown through the integration shutdown service. Msvm_ShutdownComponent.InitiateShutdown.</summary>
    Shutdown,
    /// <summary>Guest OS restart through the integration shutdown service. Msvm_ShutdownComponent.InitiateReboot (WS2016+).</summary>
    Restart,
    /// <summary>Immediate power off, equivalent to pulling the plug. RequestStateChange(3). May lose data.</summary>
    TurnOff,
    /// <summary>Hard reset, equivalent to the reset button. RequestStateChange(11). May lose data.</summary>
    Reset,
    /// <summary>Pause CPU execution. RequestStateChange(9).</summary>
    Pause,
    /// <summary>Resume from Paused. RequestStateChange(2).</summary>
    Resume,
    /// <summary>Save memory state to disk and stop. RequestStateChange(6).</summary>
    Save,
}

public static class VmPowerStateMapper
{
    /// <summary>
    /// Maps Msvm_ComputerSystem.EnabledState to a normalized state.
    /// Values 2..11 come from the documented v2 EnabledState table; 327xx values are the
    /// documented DMTF-proposed/transitional values (also returned on current builds).
    /// Critical values (32781..32792) map to the same state with <c>isCritical = true</c>.
    /// </summary>
    public static VmPowerState Map(ushort enabledState, out bool isCritical)
    {
        isCritical = enabledState is >= 32781 and <= 32792;
        return enabledState switch
        {
            0 => VmPowerState.Unknown,
            1 => VmPowerState.Other,
            2 => VmPowerState.Running,
            3 => VmPowerState.Off,
            4 => VmPowerState.Stopping,
            6 => VmPowerState.Saved,
            9 => VmPowerState.Paused,
            10 => VmPowerState.Starting,
            11 => VmPowerState.Resetting,
            32768 => VmPowerState.Paused,
            32769 => VmPowerState.Saved,
            32770 => VmPowerState.Starting,
            32771 => VmPowerState.Other,      // Snapshotting
            32773 => VmPowerState.Saving,
            32774 => VmPowerState.Stopping,
            32776 => VmPowerState.Pausing,
            32777 => VmPowerState.Resuming,
            32779 => VmPowerState.Saved,      // FastSaved
            32780 => VmPowerState.Saving,     // FastSaving
            32781 => VmPowerState.Running,
            32782 => VmPowerState.Off,
            32783 => VmPowerState.Stopping,
            32784 => VmPowerState.Saved,
            32785 => VmPowerState.Paused,
            32786 => VmPowerState.Starting,
            32787 => VmPowerState.Resetting,
            32788 => VmPowerState.Saving,
            32789 => VmPowerState.Pausing,
            32790 => VmPowerState.Resuming,
            32791 => VmPowerState.Saved,
            32792 => VmPowerState.Saving,
            _ => VmPowerState.Unknown,
        };
    }
}

/// <summary>Which operations are allowed in which state, and which end state proves success.</summary>
public static class VmPowerRules
{
    public static bool IsAllowed(VmPowerOperation op, VmPowerState state) => op switch
    {
        VmPowerOperation.Start => state is VmPowerState.Off or VmPowerState.Saved,
        VmPowerOperation.Shutdown => state is VmPowerState.Running,
        VmPowerOperation.Restart => state is VmPowerState.Running,
        VmPowerOperation.TurnOff => state is VmPowerState.Running or VmPowerState.Paused or VmPowerState.Starting or VmPowerState.Stopping,
        VmPowerOperation.Reset => state is VmPowerState.Running,
        VmPowerOperation.Pause => state is VmPowerState.Running,
        VmPowerOperation.Resume => state is VmPowerState.Paused,
        VmPowerOperation.Save => state is VmPowerState.Running or VmPowerState.Paused,
        _ => false,
    };

    /// <summary>True when the VM is already in the state the operation would produce (no-op).</summary>
    public static bool IsAlreadyInTargetState(VmPowerOperation op, VmPowerState state) => op switch
    {
        VmPowerOperation.Start => state is VmPowerState.Running,
        VmPowerOperation.Shutdown or VmPowerOperation.TurnOff => state is VmPowerState.Off,
        VmPowerOperation.Pause => state is VmPowerState.Paused,
        VmPowerOperation.Resume => state is VmPowerState.Running,
        VmPowerOperation.Save => state is VmPowerState.Saved,
        _ => false,
    };

    /// <summary>The stable state that proves the operation completed.</summary>
    public static VmPowerState ExpectedState(VmPowerOperation op) => op switch
    {
        VmPowerOperation.Start or VmPowerOperation.Resume or VmPowerOperation.Restart or VmPowerOperation.Reset => VmPowerState.Running,
        VmPowerOperation.Shutdown or VmPowerOperation.TurnOff => VmPowerState.Off,
        VmPowerOperation.Pause => VmPowerState.Paused,
        VmPowerOperation.Save => VmPowerState.Saved,
        _ => VmPowerState.Unknown,
    };

    /// <summary>Operations that can lose guest data or interrupt running workloads without guest cooperation.</summary>
    public static bool IsDestructive(VmPowerOperation op) => op is VmPowerOperation.TurnOff or VmPowerOperation.Reset;

    /// <summary>For restart/reset the end state equals the start state, so success is proven by a reset uptime.</summary>
    public static bool RequiresUptimeProof(VmPowerOperation op) => op is VmPowerOperation.Restart or VmPowerOperation.Reset;

    /// <summary>RequestStateChange value for operations implemented that way; null for shutdown-component operations.</summary>
    public static ushort? RequestedStateFor(VmPowerOperation op) => op switch
    {
        VmPowerOperation.Start => 2,
        VmPowerOperation.Resume => 2,
        VmPowerOperation.TurnOff => 3,
        VmPowerOperation.Save => 6,
        VmPowerOperation.Pause => 9,
        VmPowerOperation.Reset => 11,
        _ => null,
    };
}
