# Proposal: failover cluster notifications through ClusAPI (not implemented)

**Status: proposal, 0.9.4. Nothing of this is in the product and nothing was tested on a cluster.** The development
machine has no failover cluster; this document compares the options, proposes a design and lists the tests that must
pass on a real cluster before any of it is called working.

## What the portal does today

- Every node agent reads `root\MSCluster` (WMI, in-process) with its inventory, every 30 s: name of the cluster, state
  and drain status of **its own** node, which VMs are roles, the shared volumes it sees.
- The gateway reads the **whole cluster** with `GetClusterStatus` (PowerShell: FailoverClusters + Storage modules)
  through one connected node agent: every `Gateway:Clusters:MonitorIntervalSeconds` (default 120 s) and when someone
  opens or refreshes the cluster page (every 20–60 s on the Storage tab since 0.9.4).
- So a node going down is seen by the portal after up to ~2 minutes (sooner for the node's own agent, whose connection
  drops), a role that fails or moves after up to ~2 minutes, and each read costs one PowerShell process on a node
  (measured fixed cost on the development machine: 1.3–1.9 s and ~140 MB, before the cluster cmdlets do anything; see
  `docs/performance-powershell.md`).

## What ClusAPI offers

`CreateClusterNotifyPortV2` (clusapi.dll, Windows Server 2012 and later) opens a notification port on a cluster handle
(`OpenCluster(NULL)` for the local cluster). The caller registers filters (`NOTIFY_FILTER_AND_TYPE`: object type +
filter flags) and blocks in `GetClusterNotifyV2`, which returns one event at a time with the object id, its name, type
and a payload buffer. Useful filters:

| Object | Filter | Tells |
|---|---|---|
| `CLUSTER_OBJECT_TYPE_NODE` | `CLUSTER_CHANGE_NODE_STATE_V2`, `..._NODE_ADDED_V2`, `..._NODE_DELETED_V2` | node up / down / paused / joining |
| `CLUSTER_OBJECT_TYPE_GROUP` | `CLUSTER_CHANGE_GROUP_STATE_V2`, `..._GROUP_OWNER_NODE_V2` | role online / offline / failed / moved to another node |
| `CLUSTER_OBJECT_TYPE_RESOURCE` | `CLUSTER_CHANGE_RESOURCE_STATE_V2`, `..._RESOURCE_OWNER_GROUP_V2` | resource failed / online (witness, IP, VM, disk) |
| `CLUSTER_OBJECT_TYPE_NETWORK`, `..._NETINTERFACE` | `CLUSTER_CHANGE_NETWORK_STATE_V2`, `..._NETINTERFACE_STATE_V2` | cluster network down / partitioned |
| `CLUSTER_OBJECT_TYPE_QUORUM` | `CLUSTER_CHANGE_QUORUM_STATE_V2` | quorum lost / regained |
| `CLUSTER_OBJECT_TYPE_SHARED_VOLUME` | `CLUSTER_CHANGE_SHARED_VOLUME_STATE_V2` | CSV state, redirected access |
| `CLUSTER_OBJECT_TYPE_CLUSTER` | `CLUSTER_CHANGE_CLUSTER_RECONNECT_V2`, `..._CLUSTER_STATE_V2`, `CLUSTER_CHANGE_CLUSTER_LOST_NOTIFICATIONS_V2` | the port reconnected to the cluster; **events were dropped** |

The names above are those of `clusapi.h` as documented by Microsoft; each one (and its numeric value) must be checked
against the Windows SDK header when the interop is written — none was compiled or run for this proposal.

Not covered by ClusAPI at all: Storage Spaces Direct (pools, virtual disks, physical disks), storage jobs, Health
Service faults, Storage Replica, free space. Those stay with the Storage module / WMI `root\Microsoft\Windows\Storage`.

## Polling and notifications compared

| | WMI / PowerShell polling (today) | ClusAPI notifications |
|---|---|---|
| Time to see a node, role or resource change | up to the polling interval (120 s by default) | as fast as the cluster service delivers the event (not measured here) |
| Cost while nothing changes | one PowerShell process per cluster per interval | one blocked thread and one open port per agent |
| Cost while much changes (drain of a node with 100 VMs) | unchanged | one event per state change: must be coalesced |
| What it can read | everything the cmdlets expose | cluster objects only; no storage health |
| Loss of information | none: each read is a full snapshot | events can be dropped (queue overflow, node of the port leaves, cluster service restart); `LOST_NOTIFICATIONS` and `RECONNECT` say so, not what was missed |
| Rights | the agent account already has cluster access (`Grant-ClusterAccess -ReadOnly` or `-Full`) | the same access is enough to open the cluster and a notify port (to be verified on a real cluster) |
| Complexity | none added | P/Invoke of `clusapi.dll` (handles, buffers that must be grown and retried, enum values), a long-lived thread, reconnection; native code inside the privileged agent |
| Failure isolation | a failing read is one failed process | a bug in the interop is inside the agent process (which also serves console and power) |
| Works for remote hosts (management agent) | no (clusters need the node agent anyway) | no |

## Proposed design (hybrid: notifications trigger, polling decides)

1. **Agent** (node agent only, behind a setting `Agent:Cluster:Notifications`, off by default until validated): a
   `ClusterNotifyListener` opens the local cluster and a notify port with the node, group, resource, network, quorum,
   shared volume and cluster filters above. It never reads state from the event payload.
2. Events are **coalesced**: any event sets a flag; at most once every 5 s the agent sends the gateway a new, tiny
   message `ClusterChanged { kinds: ["node","group",...], lost: bool }` (agent protocol 14). No state travels in it.
3. **Gateway**: on `ClusterChanged`, `ClusterMonitor` schedules a `GetClusterStatus` read of that cluster now (debounced:
   at most one read per cluster every 15 s), through the node that sent it. The snapshot, the rules and the stored
   status stay exactly as they are: the notification only chooses *when* to read.
4. **Reconciliation**: the periodic read every `MonitorIntervalSeconds` stays, whatever the notifications say. With
   notifications on and healthy it can be lengthened (for example 300 s); it is what repairs a lost event, a dead
   listener, an agent that never sent anything. `lost: true` or a reconnect of the port forces a read at once.
5. **Failure handling**: if the port cannot be opened or `GetClusterNotifyV2` fails, the listener logs it, waits with
   backoff and tries again; the portal behaves as today (polling only) and shows nothing as "live". The listener runs on
   its own thread; an exception there never stops the agent channel.
6. **Several nodes**: every node agent has its own port, so the gateway gets the same change from several nodes; the
   debounce per cluster makes them one read. If the node that reported is the one that went down, another node reports
   the same change.

What this buys: node, role, resource, network, quorum and CSV changes on the cluster page and in *Needs attention*
within seconds instead of up to two minutes, and fewer PowerShell reads while nothing happens. What it does not buy:
anything about storage health or storage jobs (still read by the snapshot), and it adds native interop to the agent.

## Recommendation

Do **not** rewrite the cluster reading around ClusAPI. Keep the snapshot as the only source of state and add the
listener only as a trigger, off by default, after the tests below pass on a real cluster. If the only goal is fresher
data, lowering `MonitorIntervalSeconds` to 60 s costs one PowerShell process per cluster per minute and needs no new
code: measure that first (real-host plan V91) — it may be enough.

## Tests required before it is enabled (real cluster, none run)

| # | Test | Expected |
|---|---|---|
| N1 | 2-node and 3-node cluster, listener on every node; stop the cluster service on one node | `ClusterChanged(node)` from the other nodes within 5 s; the portal shows the node down after one read; no duplicate reads within the debounce |
| N2 | Live-migrate a VM role; fail a VM role; take the witness offline; disable a cluster network | one coalesced message per burst with the right kinds; status and problems as with polling |
| N3 | Drain a node with 50+ VM roles | at most one message every 5 s and one read every 15 s; CPU and memory of the agent stay flat |
| N4 | Restart the cluster service on the node of the listener; pause/resume that node; reboot it | the listener reconnects by itself; `lost: true` or a reconnect forces a read; nothing stays stale |
| N5 | Overflow the port (stop reading it for a minute during a drain, with a test hook) | `CLUSTER_CHANGE_CLUSTER_LOST_NOTIFICATIONS_V2` is received and forces a read |
| N6 | Agent account with `Grant-ClusterAccess -ReadOnly` only | the port opens and delivers events; if not, the documented right is updated |
| N7 | Listener disabled (default) and listener failing to start | behaviour identical to 0.9.4: polling only, no error shown to users, a warning in the agent log |
| N8 | Workgroup cluster (DNS access point), Windows Server 2019, 2022 and 2025 | the same results on each |
| N9 | Agent stopped for 10 minutes while changes happen | on reconnect the periodic read shows the present state; nothing depends on missed events |
| N10 | 24 h soak with the listener on | no handle or memory growth in the agent (`Get-Process` handles / working set before and after) |

Unit tests that can be written without a cluster (and should be, with the listener): coalescing and debounce (pure
logic), the protocol 14 message and its version gate, the gateway scheduling a read on `ClusterChanged`, the fallback
when no message ever arrives.
