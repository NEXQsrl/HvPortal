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

## Later milestones (research notes; not implemented)

| Function | Planned API | Notes |
|---|---|---|
| CPU/RAM changes | `Msvm_VirtualSystemManagementService.ModifyResourceSettings` | running-VM limits: memory hot-add for static memory Gen2 + WS2016+, vCPU change only when off |
| Disks / DVD | `AddResourceSettings` / `RemoveResourceSettings`, `Msvm_ImageManagementService` (Create/Resize/ValidateVirtualHardDisk) | path validation, reparse point checks, ownership of VHDX vs other VMs |
| Checkpoints | `Msvm_VirtualSystemSnapshotService` (CreateSnapshot, ApplySnapshot, DestroySnapshot); `Msvm_VirtualSystemSettingData.UserSnapshotType` (production / production-only / standard) | merge monitoring through `OperationalStatus[1]=32772` |
| Native VM groups | `Msvm_VirtualSystemCollection`, `Msvm_CollectionReferencePoint`… | `CollectionType` VM vs management collection |
| Switches / NICs | `Msvm_VirtualEthernetSwitchManagementService`, `Msvm_EthernetPortAllocationSettingData`, VLAN `Msvm_EthernetSwitchPortVlanSettingData` | changes that can cut management require pre-checks and rollback timer |
| Live/storage migration | `Msvm_VirtualSystemMigrationService` | cluster: `root\MSCluster` `MSCluster_ResourceGroup.Move` |
| Replica | `Msvm_ReplicationService` | M3+ |
| Backup | `Msvm_VirtualSystemSnapshotService` (recovery snapshots), `Msvm_VirtualSystemReferencePointService`, RCT via `Msvm_ImageManagementService` / `GetVirtualDiskChanges`, VSS | consistency levels must be reported as obtained |
