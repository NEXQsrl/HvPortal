// Types of the operations API (groups, VM configuration, creation, checkpoints, host resources, console).
// Values of `config` come from the agent's Hyper-V module (GetVmConfig): memory in bytes.
import type { PowerState } from './types';

export interface Group {
  id: string;
  parentId: string | null;
  name: string;
  description: string | null;
  createdAt: string;
  vmIds: string[];
  /** The folder has limits of its own (quotas, hosts, networks, storage). */
  limited?: boolean;
  /** The user may create VMs inside the folder on the hosts opened by its limits. */
  canCreateVm?: boolean;
}

export interface GroupsResponse { canManage: boolean; groups: Group[] }

export interface VmConfigAdapter { id: string; name: string; switchName: string | null; macAddress?: string | null; dynamicMac?: boolean; vlanId: number; vlanMode?: string }
export interface VmConfigDrive { controllerType: string; controllerNumber: number; controllerLocation: number; path: string | null }

export interface VmConfig {
  name: string;
  notes: string | null;
  generation: number;
  state: string;
  version: string;
  configurationLocation?: string | null;
  processorCount: number;
  processorReserve?: number;
  processorLimit?: number;
  processorWeight?: number;
  memoryStartupBytes: number;
  dynamicMemoryEnabled: boolean;
  memoryMinimumBytes?: number;
  memoryMaximumBytes?: number;
  memoryBuffer?: number;
  memoryPriority?: number;
  automaticStartAction?: string;
  automaticStartDelay?: number;
  automaticStopAction?: string;
  checkpointType?: string;
  secureBootEnabled: boolean | null;
  secureBootTemplate: string | null;
  tpmEnabled: boolean | null;
  dvdDrives: VmConfigDrive[];
  networkAdapters: VmConfigAdapter[];
  hardDisks: VmConfigDrive[];
}

export interface ConfigOptions { startActions: string[]; stopActions: string[]; checkpointTypes: string[]; secureBootTemplates: string[] }

export interface ConfigResponse {
  config: VmConfig;
  powerState: PowerState;
  can: { compute: boolean; storage: boolean; network: boolean; annotate?: boolean };
  requiresOff: string[];
  switches: string[];
  isoLibrary: string[];
  options: ConfigOptions;
}

/** PATCH /vms/{id}/config body: only changed fields are sent. */
export interface ConfigChange {
  newName?: string;
  notes?: string;
  processorCount?: number;
  processorReserve?: number;
  processorLimit?: number;
  processorWeight?: number;
  memoryStartupMb?: number;
  dynamicMemoryEnabled?: boolean;
  memoryMinimumMb?: number;
  memoryMaximumMb?: number;
  memoryBuffer?: number;
  memoryPriority?: number;
  automaticStartAction?: string;
  automaticStartDelaySeconds?: number;
  automaticStopAction?: string;
  checkpointType?: string;
  secureBootEnabled?: boolean;
  secureBootTemplate?: string;
  dvdIsoPath?: string;
  networkAdapterChanges?: { adapterId: string; switchName?: string | null; vlanId?: number | null }[];
  addNetworkAdapterSwitch?: string;
  newDisk?: { sizeGb: number; fixed: boolean };
  expandDisk?: { path: string; newSizeGb: number };
}

export interface CheckpointInfo { id: string; name: string; parentId: string | null; createdUtc: string; type: string | null; isCurrent: boolean }
export interface CheckpointsResponse { checkpoints: CheckpointInfo[]; powerState: PowerState; canCreate: boolean; canApply: boolean; applyWhileRunning?: boolean; note: string }

export interface SwitchInfo {
  id: string; name: string; type: string; netAdapter: string | null; allowManagementOs: boolean; embeddedTeaming: boolean; connectedVms: string[];
  /** Private network created for a folder (counts against the folder's network quota). */
  ownerGroupId?: string | null; ownerGroupName?: string | null; canDelete?: boolean;
}
export interface NetAdapterEntry { name: string; description: string | null; status: string | null; linkSpeed: string | null; macAddress: string | null; switchName: string | null }

/** Limits of a VM folder (null/absent = no limit or no restriction). */
export interface GroupPolicy {
  maxVms?: number | null; maxProcessors?: number | null; maxMemoryMb?: number | null; maxDiskGb?: number | null; maxNetworks?: number | null;
  hosts?: string[] | null;
  networks?: { hostId: string; name: string }[] | null;
  storage?: { hostId: string; path: string }[] | null;
}
export interface GroupUsage { vms: number; processors: number; memoryMb: number; diskGb: number; diskUnknownVms: number; networks: number }
export interface GroupPolicyLevel { groupId: string; name: string; policy: GroupPolicy; usage: GroupUsage }
export interface GroupPolicyHost {
  id: string; displayName: string; connected: boolean; ready: boolean; switches: string[]; networksRestricted: boolean; storageRoots: string[];
  canCreateVm: boolean; canCreateNetwork: boolean; canBrowseFiles: boolean; canUploadFiles: boolean;
}
export interface GroupNetwork { id: string; hostId: string; hostName: string | null; name: string; switchId: string | null; groupId: string; groupName: string | null; createdAt: string; canDelete: boolean }
export interface GroupPolicyView {
  policy: GroupPolicy | null; canEdit: boolean; canCreateVm: boolean; canCreateNetwork: boolean;
  levels: GroupPolicyLevel[]; hosts: GroupPolicyHost[]; networks: GroupNetwork[];
}

export interface HostDiskInfo { path: string; vmId: string | null; vmName: string | null; controller: string | null; format: string | null; diskType: string | null; sizeBytes: number | null; fileSizeBytes: number | null; parentPath: string | null }
export interface HostDiskRow { disk: HostDiskInfo; portalVmId: string }

export interface IsoFile { path: string; sizeBytes: number; modifiedUtc: string }
export interface IsosResponse { roots: string[]; files: IsoFile[] }

/** GET /hosts/{id}/folders (agent 0.3.1+): drives when path is null, otherwise sub-folders (system/hidden excluded). */
export interface DriveEntry { path: string; label: string | null; totalBytes: number; freeBytes: number }
export interface FolderEntry { name: string; path: string }
export interface FileEntry { name: string; path: string; sizeBytes: number; modifiedUtc: string }
export interface FolderListing { path: string | null; parent: string | null; drives: DriveEntry[]; folders: FolderEntry[]; isos: IsoFile[]; files: FileEntry[]; freeBytes: number | null; truncated: boolean }

export interface HostDefaultsResponse {
  defaults: { virtualHardDiskPath: string | null; virtualMachinePath: string | null; logicalProcessorCount: number; memoryCapacityBytes: number; supportedVersions: string[]; defaultVersion: string | null };
  switches: string[];
  /** In a group: the networks are limited to those of the group; storageRoots (non-empty) are the only allowed locations. */
  networksRestricted?: boolean;
  storageRoots?: string[];
  isoLibrary: string[];
  existingNames: string[];
  options: { startActions: string[]; checkpointTypes: string[]; secureBootTemplates: string[] };
}

export interface NewVmSpec {
  name: string;
  /** Created from this template: its disks are copied (the gateway decides which files). */
  templateId?: string;
  generation: 1 | 2;
  processorCount: number;
  memoryStartupMb: number;
  dynamicMemory: boolean;
  memoryMinimumMb?: number;
  memoryMaximumMb?: number;
  diskSizeGb: number;
  diskFixed: boolean;
  switchName?: string;
  isoPath?: string;
  secureBootTemplate?: string;
  enableTpm: boolean;
  checkpointType?: string;
  automaticStartAction?: string;
  startAfterCreate: boolean;
  notes?: string;
  /** Folder that will contain <name>\ (null = host defaults). */
  location?: string | null;
}

export interface Accepted { jobId: string; created: boolean }

/** A VM template: copies of the disks of a prepared VM on one host, with the hardware of that VM. */
export interface VmTemplate {
  id: string;
  hostId: string;
  hostName: string;
  name: string;
  description: string | null;
  status: 'Creating' | 'Ready' | 'Failed' | 'Unverified' | 'Deleting';
  error: string | null;
  sourceVmName: string | null;
  generation: number;
  processorCount: number;
  memoryStartupMb: number;
  dynamicMemory: boolean;
  memoryMinimumMb: number | null;
  memoryMaximumMb: number | null;
  secureBootTemplate: string | null;
  tpmEnabled: boolean;
  disks: { file: string; sizeBytes: number; fileBytes: number }[];
  /** Size of the boot disk: the smallest disk a VM created from the template can have. */
  bootDiskGb: number;
  sizeBytes: number;
  createdAt: string;
  jobId: string | null;
  folderPath: string | null;
  can: { deploy: boolean; manage: boolean; delete: boolean };
}
