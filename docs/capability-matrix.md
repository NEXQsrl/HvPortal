# Capability matrix

Legend — **Impl**: implemented in code · **Unit**: automated tests without Hyper-V · **Host**: validated on a real
Hyper-V host with VMs. "Host: pending" means the code path calls the documented API but has not yet been executed
against real VMs (see [real-host test plan](verification/real-host-test-plan.md)).

## Milestone 1

| Function | API (namespace `root\virtualization\v2` unless noted) | Min. version (per docs) | Privilege | Impl | Unit | Host |
|---|---|---|---|---|---|---|
| Detect Hyper-V / access | `Win32_Service` (vmms) in `root\cimv2`; `Msvm_VirtualSystemManagementService` count | WS2012 | none / Hyper-V Admins | ✅ | ✅ | ✅ non-admin → `AccessDenied` observed on Windows 11 26200 |
| Host facts | `Win32_OperatingSystem`, `Win32_ComputerSystem` (`root\cimv2`) | any | Users | ✅ | — | ✅ (Windows 11 26200) |
| List VMs | `Msvm_ComputerSystem` (filter `Name` = GUID) | WS2012 | Hyper-V Admins | ✅ | ✅ mapping | pending |
| Generation / config version | `Msvm_VirtualSystemSettingData` (`VirtualSystemType='Microsoft:Hyper-V:System:Realized'`, `VirtualSystemSubType`, `Version`) | WS2012 R2 (Gen2) | Hyper-V Admins | ✅ | ✅ parse | pending |
| vCPU / memory config | `Msvm_ProcessorSettingData.VirtualQuantity`, `Msvm_MemorySettingData.VirtualQuantity/DynamicMemoryEnabled` | WS2012 | Hyper-V Admins | ✅ | ✅ InstanceID parse | pending |
| Runtime stats | `Msvm_VirtualSystemManagementService.GetSummaryInformation` (0,1,4,101,103,104,105,106,109) | WS2012 | Hyper-V Admins | ✅ | — | pending |
| Guest shutdown availability | `Msvm_ShutdownComponent.OperationalStatus[0]==2` | WS2012 | Hyper-V Admins | ✅ | — | pending |
| Start / Resume | `Msvm_ComputerSystem.RequestStateChange(2)` + `Msvm_ConcreteJob` | WS2012 | Hyper-V Admins | ✅ | ✅ codes | pending |
| Turn off | `RequestStateChange(3)` | WS2012 | Hyper-V Admins | ✅ | ✅ | pending |
| Save | `RequestStateChange(6)` | WS2012 | Hyper-V Admins | ✅ | ✅ | pending |
| Pause | `RequestStateChange(9)` | WS2012 | Hyper-V Admins | ✅ | ✅ | pending |
| Reset | `RequestStateChange(11)` | WS2012 | Hyper-V Admins | ✅ | ✅ | pending |
| Guest shutdown | `Msvm_ShutdownComponent.InitiateShutdown(Force=false, Reason)` | WS2012 | Hyper-V Admins | ✅ | — | pending |
| Guest restart | `Msvm_ShutdownComponent.InitiateReboot(Force=false, Reason)` | **WS2016** | Hyper-V Admins | ✅ | — | pending |
| Out-of-portal change detection | periodic inventory diff (30 s) | — | — | ✅ | ✅ (via ingestion) | pending |

State mapping (`EnabledState`): 2 Running, 3 Off, 4/32774 Stopping, 6/32769/32779 Saved, 9/32768 Paused,
10/32770 Starting, 11 Resetting, 32773/32780 Saving, 32776 Pausing, 32777 Resuming, 32781–32792 critical variants.
Success proof: target state reached; for restart/reset additionally `OnTimeInMilliseconds`/`UpTime` lower than the
time since dispatch (**heuristic to be confirmed on real hosts**; if not observed the job ends `Unverified`).

Return codes mapped: 0 ok, 4096 job started, 32769 access denied, 32770 not supported, 32772 timeout,
32775 invalid state, 32781 guest locked (needs force), others → failed with the numeric code shown.

Availability by state (enforced in UI, API with fresh data, and agent):

| Operation | Allowed when | Destructive |
|---|---|---|
| Start | Off, Saved | no |
| Shutdown / Restart | Running + shutdown integration service OK | no (guest may refuse) |
| Turn off | Running, Paused, Starting, Stopping | **yes** (typed confirmation) |
| Reset | Running | **yes** (typed confirmation) |
| Pause | Running | no |
| Resume | Paused | no |
| Save | Running, Paused | no |

## v0.3 — configuration, creation, checkpoints, host resources, console, groups

Operations marked *PS* run in the fixed script `Ops\HvPortal.Ops.ps1` (Hyper-V PowerShell module, ADR-012).
"Unit" means gateway/agent automated tests with a test double for the host; the smoke test additionally verifies the
real gateway → agent → PowerShell path on a machine without Hyper-V rights (the script returns the real error).

| Function | API | Privilege | Online / offline | Impl | Unit | Host |
|---|---|---|---|---|---|---|
| Read VM configuration | *PS* `Get-VM`, `Get-VMProcessor`, `Get-VMMemory`, `Get-VMFirmware`, `Get-VMSecurity`, `Get-VMDvdDrive`, `Get-VMNetworkAdapter(+Vlan)`, `Get-VMHardDiskDrive` | Hyper-V Admins | both | ✅ | ✅ | pending |
| Name, notes, auto start/stop, checkpoint type | *PS* `Set-VM`, `Rename-VM` | Hyper-V Admins | both | ✅ | ✅ | pending |
| vCPU count | *PS* `Set-VMProcessor -Count` | Hyper-V Admins | **off only** (refused by API when the VM is not Off) | ✅ | ✅ | pending |
| CPU reserve / limit / weight | *PS* `Set-VMProcessor` | Hyper-V Admins | both (Hyper-V decides) | ✅ | ✅ | pending |
| Startup memory, dynamic memory min/max/buffer, weight | *PS* `Set-VMMemory` | Hyper-V Admins | toggling dynamic memory **off only**; runtime resize per Hyper-V rules | ✅ | ✅ | pending |
| Secure Boot on/off, template | *PS* `Set-VMFirmware` (Gen2) | Hyper-V Admins | **off only** | ✅ | ✅ | pending |
| DVD: mount ISO from host library / eject | *PS* `Set-VMDvdDrive`, `Add-VMDvdDrive` | Hyper-V Admins | both | ✅ | ✅ | pending |
| vNIC: switch, disconnect, VLAN access/untagged, add vNIC | *PS* `Connect/Disconnect-VMNetworkAdapter`, `Set-VMNetworkAdapterVlan`, `Add-VMNetworkAdapter` | Hyper-V Admins | both (hot add per Hyper-V rules) | ✅ | ✅ | pending |
| New VHDX attached to the VM | *PS* `New-VHD` in `Get-VMHost.VirtualHardDiskPath`, `Add-VMHardDiskDrive` (file removed if attach fails) | Hyper-V Admins | SCSI hot add per Hyper-V rules | ✅ | ✅ | pending |
| Expand VHDX | *PS* `Resize-VHD` (grow only) | Hyper-V Admins | online for SCSI VHDX; not with checkpoints | ✅ | ✅ | pending |
| Create VM (Gen1/Gen2, CPU, memory, disk, switch, ISO + first boot, Secure Boot template, vTPM, start) | *PS* `New-VM`, `Set-VMProcessor`, `Set-VMMemory`, `Set-VMFirmware`, `Set-VMKeyProtector -NewLocalKeyProtector`, `Enable-VMTPM`, `Start-VM` | Hyper-V Admins | — | ✅ | ✅ | pending |
| Checkpoints: list (tree), create, apply, delete (+children) | *PS* `Get-VMSnapshot`, `Checkpoint-VM`, `Restore-VMSnapshot`, `Remove-VMSnapshot` | Hyper-V Admins | apply: API requires Off/Saved | ✅ | ✅ | pending |
| Host defaults, switches, VM disks, ISO library listing | *PS* `Get-VMHost`, `Get-VMHostSupportedVersion`, `Get-VMSwitch`, `Get-VHD`, `Get-ChildItem *.iso` (library roots only, depth 2, no reparse points) | Hyper-V Admins | — | ✅ | ✅ | pending |
| Console screen | `Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage` + `Msvm_VideoHead` | Hyper-V Admins | running VMs | ✅ | ✅ relay | pending |
| Console keyboard / Ctrl+Alt+Del / type text | `Msvm_Keyboard.TypeScancodes`, `TypeCtrlAltDel`, `TypeText` | Hyper-V Admins | running VMs | ✅ | ✅ relay | pending |
| Console mouse | `Msvm_SyntheticMouse.SetAbsolutePosition`, `SetButtonState` | Hyper-V Admins | integration services running | ✅ | ✅ relay | pending |
| Portal groups (folders), nesting, membership, RBAC inheritance | portal database | `groups.manage` (+`roles.manage` for access changes) | — | ✅ | ✅ | n/a |
| VM summary: creation time, configuration path, Hyper-V notes, replication mode/state/health, memory demand — agent 0.3.4 | `GetSummaryInformation` RequestedInformation 2, 3, 112, 113, 114, 115, 116; `Msvm_VirtualSystemSettingData.ConfigurationDataRoot` (learn.microsoft.com hyperv_v2/getsummaryinformation, msvm-summaryinformation) | Hyper-V Admins | MemoryAvailable/AvailableMemoryBuffer valid only with dynamic memory; demand = assigned × (100 − MemoryAvailable) / 100; ReplicationState/Health are the deprecated single-relationship values (Ex arrays not used yet) | ✅ | ✅ (ingestion) | pending (V27) |
| Host file explorer (browse, download, upload) — agent 0.3.3 | .NET `DirectoryInfo`/`FileStream` in the agent (no PowerShell) | agent service account (NTFS ACLs apply) | local fixed drives only; system/hidden/linked excluded | ✅ | ✅ (also e2e against the real agent on the dev machine) | pending (V21–V23) |
| Virtual switches: create (External / Internal / Private), remove — agent 0.5.0 | `New-VMSwitch`, `Remove-VMSwitch`, `Get-NetAdapter -Physical` (fixed script) | Hyper-V Administrators (external switches change the host network configuration) | SET teaming and VLAN on the host adapter not managed; delete refused while VMs are connected | ✅ | ✅ (validation, authorization, quotas, reconciliation logic) | pending (V38) |
| Apply checkpoint on a running VM — agent 0.5.0 | `Stop-VM -TurnOff` then `Restore-VMSnapshot` | Hyper-V Administrators | current state discarded (typed VM name) | ✅ | ✅ | pending (V39) |
| VM templates: create from a VM that is off, create a VM from a template, delete — agent 0.6.0 | `Get-VMHardDiskDrive`, `Get-VHD` (size, differencing), `Get-VMSnapshot`, `Copy-Item` of the .vhdx/.vhd files, `New-VM -VHDPath`, `Resize-VHD`, `Add-VMHardDiskDrive -ControllerType SCSI`, `Remove-Item` of the template disk files (fixed script) | Hyper-V Administrators; the agent account needs modify rights on the template library folder and on the VM location | the source VM is only read; a template is deleted with a typed name | ✅ | ✅ | pending (V46-V48) |
| Native VM groups (mirror of portal groups, import) — agent 0.3.2 | `Get-VMGroup`, `New-VMGroup -Name -GroupType -Id`, `Rename-VMGroup -VMGroup -NewName`, `Add-VMGroupMember` / `Remove-VMGroupMember -VMGroup (-VM \| -VMGroupMember)`, `Remove-VMGroup -VMGroup -Force` (group must be empty). Sources: learn.microsoft.com/powershell/module/hyper-v/{new,get,rename,remove}-vmgroup, {add,remove}-vmgroupmember (WS2016–2025) | Hyper-V Admins (to be confirmed on a real host) | ManagementCollectionType contains only VMCollectionType groups (documented); `VMMembers` / `VMGroupMembers` properties; the id property (`InstanceId`) is read defensively | ✅ | ✅ (fake agent) | pending (V17–V20) |
| Remote hosts through a management agent (ADR-022) — agent 0.4.0 | same WMI classes over DCOM with `ConnectionOptions.Username/SecurePassword` + packet privacy; typed operations through `Invoke-Command -ComputerName -Credential [-UseSSL] -FilePath HvPortal.Ops.ps1`; files through `\\host\X$` under `LogonUser(LOGON32_LOGON_NEW_CREDENTIALS)`; drives from `Win32_LogicalDisk` (learn.microsoft.com: connectionoptions, invoke-command, logonuserw) | administrator account of the host (UAC remote restrictions: built-in Administrator, or `LocalAccountTokenFilterPolicy = 1`) | firewall WMI/DCOM, WinRM 5985/5986, SMB 445 from the management server; WinRM HTTP in a workgroup needs TrustedHosts; no IPv6 literals | ✅ | ✅ (fake agent) | pending (V29–V34) |

Not in v0.3 (still research): boot order editing, TPM enable on existing VMs, controller management, VHD removal /
detach, NAT/QoS/SR-IOV, migration, Replica, backup.

## Later milestones (research notes; not implemented)

| Function | Planned API | Notes |
|---|---|---|
| Disks / DVD | `AddResourceSettings` / `RemoveResourceSettings`, `Msvm_ImageManagementService` (Create/Resize/ValidateVirtualHardDisk) | path validation, reparse point checks, ownership of VHDX vs other VMs |
| Checkpoints | `Msvm_VirtualSystemSnapshotService` (CreateSnapshot, ApplySnapshot, DestroySnapshot); `Msvm_VirtualSystemSettingData.UserSnapshotType` (production / production-only / standard) | merge monitoring through `OperationalStatus[1]=32772` |
| Native VM groups | `Msvm_VirtualSystemCollection`, `Msvm_CollectionReferencePoint`… | `CollectionType` VM vs management collection |
| Switches / NICs | `Msvm_VirtualEthernetSwitchManagementService`, `Msvm_EthernetPortAllocationSettingData`, VLAN `Msvm_EthernetSwitchPortVlanSettingData` | changes that can cut management require pre-checks and rollback timer |
| Live/storage migration | `Msvm_VirtualSystemMigrationService` | cluster: `root\MSCluster` `MSCluster_ResourceGroup.Move` |
| Replica | `Msvm_ReplicationService` | M3+ |
| Backup | `Msvm_VirtualSystemSnapshotService` (recovery snapshots), `Msvm_VirtualSystemReferencePointService`, RCT via `Msvm_ImageManagementService` / `GetVirtualDiskChanges`, VSS | consistency levels must be reported as obtained |
