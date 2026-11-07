#Requires -Version 5.1
<#
  HvPortal agent operations (Windows PowerShell 5.1, Hyper-V module).
  Invoked ONLY by HvPortal.Agent: one JSON request on stdin -> one JSON response on stdout.
  Security rules:
   - closed set of operations (switch below); anything else is refused;
   - every value is cast to an explicit type and passed as a cmdlet parameter (splatting of fixed keys);
   - no Invoke-Expression, no script blocks built from input, no Start-Process;
   - VMs are always resolved by Id (names are not unique in Hyper-V);
   - new files are created under the host default folders (Get-VMHost) or, for NewVm, under <location>\<VM name>\ where
     location was validated by the agent (local fixed drive, no system/hidden folders, no junctions or symbolic links);
   - ISO paths must be inside the allowed library roots provided by the agent;
   - VM groups (SyncVmGroups): only groups whose id the gateway lists as portal-owned are renamed, re-membered or removed;
   - templates: a template is the folder <template library>\<template id (guid)> holding copies of virtual disks. The
     library folders come from the agent (validated like a VM location); NewTemplate only creates that folder,
     RemoveTemplate only deletes disk files directly inside it, NewVm only reads disks from it (they are copied).
  Remote hosts (management agent): HvPortal.Remote.ps1 runs THIS file on the host with Invoke-Command -FilePath and passes
  the same JSON request as the only argument; the response is then written to the pipeline instead of the console.
#>
param([string]$RequestJson)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
$script:RemoteRequest = -not [string]::IsNullOrEmpty($RequestJson)
if (-not $script:RemoteRequest) {
    # UTF-8 both ways (Windows PowerShell 5.1 defaults to the OEM code page).
    [Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
}

function Write-Response([bool]$ok, $result, [string]$errorText, $applied) {
    $obj = @{ ok = $ok; result = $result; error = $errorText; applied = @($applied) }
    $json = ConvertTo-Json -InputObject $obj -Depth 8 -Compress
    if ($script:RemoteRequest) { Write-Output $json } else { [Console]::Out.Write($json) }
}

function Get-VmById([string]$id) {
    $g = [guid]$id
    $vm = Get-VM -Id $g -ErrorAction SilentlyContinue
    if (-not $vm) { throw "VM $g not found on this host." }
    return $vm
}

function Get-SafeName([string]$name) {
    $s = ($name -replace '[^A-Za-z0-9_.-]', '_').Trim('.', ' ')
    if ($s.Length -eq 0) { $s = 'vm' }
    if ($s.Length -gt 60) { $s = $s.Substring(0, 60) }
    return $s
}

function Test-IsoPath([string]$path, $roots) {
    if ([string]::IsNullOrWhiteSpace($path)) { return }
    if (-not $path.EndsWith('.iso', [StringComparison]::OrdinalIgnoreCase)) { throw 'Only .iso files can be mounted.' }
    $full = [IO.Path]::GetFullPath($path)
    $inside = $false
    foreach ($r in @($roots)) {
        if ([string]::IsNullOrWhiteSpace($r)) { continue }
        $root = [IO.Path]::GetFullPath(([string]$r).TrimEnd('\') + '\')
        if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $inside = $true; break }
    }
    if (-not $inside) { throw 'The ISO file is not inside an allowed ISO library folder of this host.' }
    $item = Get-Item -LiteralPath $full -ErrorAction SilentlyContinue
    if (-not $item -or $item.PSIsContainer) { throw "ISO file not found: $full" }
    $p = $item
    while ($p) {
        if ($p.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'ISO paths through junctions or symbolic links are not allowed.' }
        $p = if ($p -is [IO.FileInfo]) { $p.Directory } else { $p.Parent }
    }
}

function Assert-NoReparsePoint($item) {
    $p = $item
    while ($p) {
        if ($p.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Paths through junctions or symbolic links are not allowed.' }
        $p = if ($p -is [IO.FileInfo]) { $p.Directory } else { $p.Parent }
    }
}

# <template library>\<guid>: returns the full path. The library folder is created only when asked (first template).
function Get-TemplateFolder([string]$folder, $roots, [switch]$CreateLibrary) {
    $full = [IO.Path]::GetFullPath($folder).TrimEnd('\')
    $library = [IO.Path]::GetDirectoryName($full)
    $id = [guid]::Empty
    if (-not $library -or -not [guid]::TryParse([IO.Path]::GetFileName($full), [ref]$id)) { throw 'Invalid template folder.' }
    $inside = $false
    foreach ($r in @($roots)) {
        if ([string]::IsNullOrWhiteSpace($r)) { continue }
        if ([string]::Equals([IO.Path]::GetFullPath([string]$r).TrimEnd('\'), $library, [StringComparison]::OrdinalIgnoreCase)) { $inside = $true; break }
    }
    if (-not $inside) { throw 'The template folder is not inside the template library of this host.' }
    if (-not (Test-Path -LiteralPath $library)) {
        if (-not $CreateLibrary) { throw "Template library folder not found: $library" }
        try { New-Item -ItemType Directory -Path $library -Force | Out-Null }
        catch { throw "Cannot create the template library folder $library with the agent service account: $($_.Exception.Message)" }
    }
    Assert-NoReparsePoint (Get-Item -LiteralPath $library -Force)
    return $full
}

# A disk file of a template (.vhdx / .vhd directly inside a template folder): returns the file item.
function Get-TemplateDisk([string]$path, $roots) {
    $full = [IO.Path]::GetFullPath($path)
    $ext = [IO.Path]::GetExtension($full)
    if ($ext -ine '.vhdx' -and $ext -ine '.vhd') { throw 'Template disks must be .vhdx or .vhd files.' }
    [void](Get-TemplateFolder ([IO.Path]::GetDirectoryName($full)) $roots)
    $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
    if (-not $item -or $item.PSIsContainer) { throw "Template disk not found: $full" }
    Assert-NoReparsePoint $item
    return $item
}

function Get-GroupId($g) {
    # The VMGroup object exposes its id as InstanceId (Get-VMGroup -Id / New-VMGroup -Id use the same value).
    if ($g.InstanceId) { return [string]$g.InstanceId }
    if ($g.Id) { return [string]$g.Id }
    throw "Cannot read the id of VM group '$($g.Name)'."
}

function ConvertTo-GroupInfo($g) {
    return @{
        id = (Get-GroupId $g); name = $g.Name; groupType = "$($g.GroupType)"
        vmIds = @($g.VMMembers | Where-Object { $_ } | ForEach-Object { "$($_.Id)" })
        groupIds = @($g.VMGroupMembers | Where-Object { $_ } | ForEach-Object { Get-GroupId $_ })
    }
}

function Get-Config($vm) {
    $mem = Get-VMMemory -VM $vm
    $cpu = Get-VMProcessor -VM $vm
    $sbEnabled = $null; $sbTemplate = $null; $tpm = $null
    if ($vm.Generation -eq 2) {
        $fw = Get-VMFirmware -VM $vm
        $sbEnabled = ("$($fw.SecureBoot)" -eq 'On')
        $sbTemplate = "$($fw.SecureBootTemplate)"
        try { $tpm = [bool](Get-VMSecurity -VM $vm).TpmEnabled } catch { $tpm = $null }
    }
    $dvds = @(Get-VMDvdDrive -VM $vm | ForEach-Object {
        @{ controllerType = "$($_.ControllerType)"; controllerNumber = [int]$_.ControllerNumber; controllerLocation = [int]$_.ControllerLocation; path = $_.Path }
    })
    $nics = @(Get-VMNetworkAdapter -VM $vm | ForEach-Object {
        $vlan = Get-VMNetworkAdapterVlan -VMNetworkAdapter $_
        $vid = 0
        if ("$($vlan.OperationMode)" -eq 'Access') { $vid = [int]$vlan.AccessVlanId }
        @{ id = "$($_.Id)"; name = $_.Name; switchName = $_.SwitchName; macAddress = $_.MacAddress; dynamicMac = [bool]$_.DynamicMacAddressEnabled; vlanId = $vid; vlanMode = "$($vlan.OperationMode)" }
    })
    $disks = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object {
        @{ controllerType = "$($_.ControllerType)"; controllerNumber = [int]$_.ControllerNumber; controllerLocation = [int]$_.ControllerLocation; path = $_.Path }
    })
    return @{
        name = $vm.Name; notes = $vm.Notes; generation = [int]$vm.Generation; state = "$($vm.State)"; version = "$($vm.Version)"
        configurationLocation = $vm.ConfigurationLocation
        processorCount = [int]$cpu.Count; processorReserve = [int]$cpu.Reserve; processorLimit = [int]$cpu.Maximum; processorWeight = [int]$cpu.RelativeWeight
        memoryStartupBytes = [int64]$mem.Startup; dynamicMemoryEnabled = [bool]$mem.DynamicMemoryEnabled
        memoryMinimumBytes = [int64]$mem.Minimum; memoryMaximumBytes = [int64]$mem.Maximum; memoryBuffer = [int]$mem.Buffer; memoryPriority = [int]$mem.Priority
        automaticStartAction = "$($vm.AutomaticStartAction)"; automaticStartDelay = [int]$vm.AutomaticStartDelay; automaticStopAction = "$($vm.AutomaticStopAction)"
        checkpointType = "$($vm.CheckpointType)"; secureBootEnabled = $sbEnabled; secureBootTemplate = $sbTemplate; tpmEnabled = $tpm
        dvdDrives = $dvds; networkAdapters = $nics; hardDisks = $disks
    }
}

# ---------------------------------------------------------------- backups (protocol 9)
# A repository is a local folder or an SMB share. With credentials (NAS outside the domain) the share is opened as a
# PowerShell drive of this process only, with the credentials of the repository; Hyper-V itself (vmms) cannot use them,
# so exports and imports go through the local staging folder. Credentials are never written anywhere nor echoed.

function Assert-StorageFolder([string]$path, [string]$what) {
    if ($script:StorageError) { throw "location_not_allowed: $script:StorageError" }
    if ($script:StorageFolders.Count -eq 0) { return }
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
    foreach ($f in $script:StorageFolders) {
        if ($full -ieq $f -or $full.StartsWith($f + '\', [StringComparison]::OrdinalIgnoreCase)) { return }
    }
    throw "location_not_allowed: $what ($full) is outside the folders the portal may use on this host ($($script:StorageFolders -join ', ')): choose a folder inside them."
}

function ConvertTo-Secret([string]$text) {
    # Built character by character: ConvertTo-SecureString -AsPlainText would put the password in the parameter
    # binding record of PowerShell module logging (event 4103) when that logging is enabled on the host.
    $secure = New-Object System.Security.SecureString
    foreach ($ch in $text.ToCharArray()) { $secure.AppendChar($ch) }
    $secure.MakeReadOnly()
    return $secure
}

function Open-BackupTarget($t) {
    $path = [string]$t.path
    if ($path.StartsWith('\\')) {
        if ($t.userName) {
            $secure = ConvertTo-Secret ([string]$t.password)
            $credential = New-Object System.Management.Automation.PSCredential ([string]$t.userName, $secure)
            $name = 'HVPB' + [guid]::NewGuid().ToString('N').Substring(0, 8)
            try { New-PSDrive -Name $name -PSProvider FileSystem -Root $path -Credential $credential -Scope Script | Out-Null }
            catch { throw "Cannot open the repository $path with the stored credentials: $($_.Exception.Message)" }
            return @{ root = "${name}:\"; drive = $name; direct = $false; share = $true }
        }
        if (-not (Test-Path -LiteralPath $path)) { throw "The repository $path cannot be reached with the computer account of this host." }
        return @{ root = $path; drive = $null; direct = $true; share = $true }
    }
    if (-not (Test-Path -LiteralPath $path)) {
        try { New-Item -ItemType Directory -Path $path -Force | Out-Null }
        catch { throw "Cannot create the repository folder $path with the agent service account: $($_.Exception.Message)" }
    }
    return @{ root = $path; drive = $null; direct = $true; share = $false }
}

function Close-BackupTarget($repo) {
    if ($repo -and $repo.drive) { Remove-PSDrive -Name $repo.drive -Force -ErrorAction SilentlyContinue }
}

function Assert-PointFolder([string]$folder, [guid]$pointId) {
    if ($folder -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\\[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' -or
        -not $folder.EndsWith('\' + $pointId.ToString('D'))) { throw 'Invalid restore point folder.' }
    return $folder
}

function Assert-RelativePath([string]$relative) {
    if ([string]::IsNullOrEmpty($relative) -or $relative.StartsWith('\') -or $relative.Contains(':') -or $relative.Contains('/')) { throw "Invalid file in the restore point: $relative" }
    foreach ($s in $relative.Split('\')) {
        if ($s.Length -eq 0 -or $s -eq '.' -or $s -eq '..' -or $s.IndexOfAny([char[]]'*?"<>|') -ge 0) { throw "Invalid file in the restore point: $relative" }
    }
}

function Read-Manifest([string]$pointPath, [guid]$pointId) {
    $file = Join-Path $pointPath 'hvportal-backup.json'
    if (-not (Test-Path -LiteralPath $file)) { throw 'The restore point has no manifest (incomplete or not created by HvPortal).' }
    $m = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ([guid][string]$m.pointId -ne $pointId) { throw 'The manifest of the folder belongs to another restore point.' }
    foreach ($f in @($m.files)) { Assert-RelativePath ([string]$f.path) }
    return $m
}

function Get-VmFolderName([string]$name) {
    if ($name -match '^[\w .()-]{1,100}$' -and -not $name.EndsWith('.') -and -not $name.EndsWith(' ')) { return $name }
    return Get-SafeName $name
}

function Copy-PointFiles($files, [string]$from, [string]$to) {
    foreach ($f in @($files)) {
        $rel = [string]$f.path
        Assert-RelativePath $rel
        $dest = Join-Path $to $rel
        $parent = Split-Path -Path $dest -Parent
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath (Join-Path $from $rel) -Destination $dest
    }
}

# Storage cmdlets may return raw CIM numbers or names depending on the host: both are turned into names.
function ConvertTo-HealthName($v) {
    switch ("$v") { '0' { 'Healthy' } '1' { 'Warning' } '2' { 'Unhealthy' } '5' { 'Unknown' } default { "$v" } }
}

function ConvertTo-OperationalName($v) {
    switch ("$v") {
        '0' { 'Unknown' } '1' { 'Other' } '2' { 'OK' } '3' { 'Degraded' } '4' { 'Stressed' } '5' { 'Predictive Failure' } '6' { 'Error' }
        '7' { 'Non-Recoverable Error' } '10' { 'Stopped' } '11' { 'In Service' } '12' { 'No Contact' } '13' { 'Lost Communication' }
        '53264' { 'Online' } '53265' { 'Not Ready' } '53266' { 'No Media' } '53267' { 'Offline' } '53268' { 'Failed' }
        default { "$v" }
    }
}

function Get-StatusFromHealth([string]$health) {
    switch ($health) { 'Healthy' { 'Ok' } 'Warning' { 'Warning' } 'Unhealthy' { 'Critical' } default { 'Unknown' } }
}

$applied = New-Object System.Collections.ArrayList
try {
    $raw = if ($script:RemoteRequest) { $RequestJson } else { [Console]::In.ReadToEnd() }
    if ($raw.Length -gt 65536) { throw 'Request too large.' }
    $req = ConvertFrom-Json -InputObject $raw
    $a = $req.args
    # Folders of the host the portal may use (set by the agent from storage.json or the remote target, never by the
    # gateway): checked wherever this script falls back to a default folder of the host.
    $script:StorageFolders = @(@($req.storageFolders) | Where-Object { $_ } | ForEach-Object { ([string]$_).TrimEnd('\') })
    $script:StorageError = [string]$req.storageError
    Import-Module Hyper-V -ErrorAction Stop

    switch ([string]$req.op) {
        'GetHostDefaults' {
            $h = Get-VMHost
            $versions = @()
            $default = $null
            try {
                $sv = Get-VMHostSupportedVersion
                $versions = @($sv | ForEach-Object { "$($_.Version)" })
                $d = $sv | Where-Object { $_.IsDefault } | Select-Object -First 1
                if ($d) { $default = "$($d.Version)" }
            } catch { }
            Write-Response $true @{
                virtualHardDiskPath = $h.VirtualHardDiskPath; virtualMachinePath = $h.VirtualMachinePath
                logicalProcessorCount = [int]$h.LogicalProcessorCount; memoryCapacityBytes = [int64]$h.MemoryCapacity
                supportedVersions = $versions; defaultVersion = $default
            } $null @()
        }

        'GetVmConfig' {
            Write-Response $true (Get-Config (Get-VmById $a.vmId)) $null @()
        }

        'SetVmConfig' {
            $vm = Get-VmById $a.vmId
            if ($null -ne $a.notes) { Set-VM -VM $vm -Notes ([string]$a.notes); [void]$applied.Add('notes') }
            if ($a.automaticStartAction) { Set-VM -VM $vm -AutomaticStartAction ([string]$a.automaticStartAction); [void]$applied.Add('automaticStartAction') }
            if ($null -ne $a.automaticStartDelaySeconds) { Set-VM -VM $vm -AutomaticStartDelay ([int]$a.automaticStartDelaySeconds); [void]$applied.Add('automaticStartDelay') }
            if ($a.automaticStopAction) { Set-VM -VM $vm -AutomaticStopAction ([string]$a.automaticStopAction); [void]$applied.Add('automaticStopAction') }
            if ($a.checkpointType) { Set-VM -VM $vm -CheckpointType ([string]$a.checkpointType); [void]$applied.Add('checkpointType') }

            $p = @{}
            if ($null -ne $a.processorCount) { $p.Count = [int64]$a.processorCount }
            if ($null -ne $a.processorReserve) { $p.Reserve = [int64]$a.processorReserve }
            if ($null -ne $a.processorLimit) { $p.Maximum = [int64]$a.processorLimit }
            if ($null -ne $a.processorWeight) { $p.RelativeWeight = [int32]$a.processorWeight }
            if ($p.PSBase.Count -gt 0) { Set-VMProcessor -VM $vm @p; [void]$applied.Add('processor') }

            $m = @{}
            if ($null -ne $a.dynamicMemoryEnabled) { $m.DynamicMemoryEnabled = [bool]$a.dynamicMemoryEnabled }
            if ($null -ne $a.memoryStartupMb) { $m.StartupBytes = [int64]$a.memoryStartupMb * 1MB }
            if ($null -ne $a.memoryMinimumMb) { $m.MinimumBytes = [int64]$a.memoryMinimumMb * 1MB }
            if ($null -ne $a.memoryMaximumMb) { $m.MaximumBytes = [int64]$a.memoryMaximumMb * 1MB }
            if ($null -ne $a.memoryBuffer) { $m.Buffer = [int32]$a.memoryBuffer }
            if ($null -ne $a.memoryPriority) { $m.Priority = [int32]$a.memoryPriority }
            if ($m.PSBase.Count -gt 0) { Set-VMMemory -VM $vm @m; [void]$applied.Add('memory') }

            if ($null -ne $a.secureBootEnabled -or $a.secureBootTemplate) {
                if ($vm.Generation -ne 2) { throw 'Secure Boot is available only on Generation 2 VMs.' }
                $f = @{}
                if ($null -ne $a.secureBootEnabled) { $f.EnableSecureBoot = $(if ([bool]$a.secureBootEnabled) { 'On' } else { 'Off' }) }
                if ($a.secureBootTemplate) { $f.SecureBootTemplate = [string]$a.secureBootTemplate }
                Set-VMFirmware -VM $vm @f
                [void]$applied.Add('firmware')
            }

            if ($null -ne $a.dvdIsoPath) {
                $iso = [string]$a.dvdIsoPath
                Test-IsoPath $iso $a.isoRoots
                $dvd = Get-VMDvdDrive -VM $vm | Select-Object -First 1
                if ($dvd) {
                    if ($iso.Length -eq 0) { Set-VMDvdDrive -VMDvdDrive $dvd -Path $null } else { Set-VMDvdDrive -VMDvdDrive $dvd -Path $iso }
                } elseif ($iso.Length -gt 0) {
                    Add-VMDvdDrive -VM $vm -Path $iso | Out-Null
                }
                [void]$applied.Add('dvd')
            }

            foreach ($c in @($a.networkAdapterChanges)) {
                if (-not $c) { continue }
                $n = Get-VMNetworkAdapter -VM $vm | Where-Object { $_.Id -eq [string]$c.adapterId } | Select-Object -First 1
                if (-not $n) { throw "Network adapter not found: $($c.adapterId)" }
                if ($null -ne $c.switchName) {
                    if ([string]$c.switchName -eq '') { Disconnect-VMNetworkAdapter -VMNetworkAdapter $n }
                    else {
                        $sw = Get-VMSwitch -Name ([string]$c.switchName) -ErrorAction SilentlyContinue | Where-Object { $_.Name -ceq [string]$c.switchName } | Select-Object -First 1
                        if (-not $sw) { throw "Virtual switch not found: $($c.switchName)" }
                        Connect-VMNetworkAdapter -VMNetworkAdapter $n -VMSwitch $sw
                    }
                }
                if ($null -ne $c.vlanId) {
                    if ([int]$c.vlanId -eq 0) { Set-VMNetworkAdapterVlan -VMNetworkAdapter $n -Untagged }
                    else { Set-VMNetworkAdapterVlan -VMNetworkAdapter $n -Access -VlanId ([int]$c.vlanId) }
                }
                [void]$applied.Add("network:$($n.Name)")
            }

            if ($null -ne $a.addNetworkAdapterSwitch) {
                $sname = [string]$a.addNetworkAdapterSwitch
                if ($sname.Length -gt 0) {
                    $sw = Get-VMSwitch -Name $sname -ErrorAction SilentlyContinue | Where-Object { $_.Name -ceq $sname } | Select-Object -First 1
                    if (-not $sw) { throw "Virtual switch not found: $sname" }
                    $added = Add-VMNetworkAdapter -VM $vm -SwitchName $sw.Name -Passthru
                    if ($null -ne $a.addNetworkAdapterVlanId) {
                        # Protocol 9: the VLAN of the portal network; never leave the new adapter untagged on the switch.
                        try { Set-VMNetworkAdapterVlan -VMNetworkAdapter $added -Access -VlanId ([int]$a.addNetworkAdapterVlanId) }
                        catch { Disconnect-VMNetworkAdapter -VMNetworkAdapter $added; throw "The adapter was added but its VLAN could not be set (it was disconnected): $($_.Exception.Message)" }
                        [void]$applied.Add("vlan:$([int]$a.addNetworkAdapterVlanId)")
                    }
                } else {
                    Add-VMNetworkAdapter -VM $vm
                }
                [void]$applied.Add('addNetworkAdapter')
            }

            if ($a.newDisk) {
                $root = (Get-VMHost).VirtualHardDiskPath
                if ($script:StorageFolders.Count -gt 0) {
                    # Restricted host: the new disk goes next to the first disk of the VM (inside the allowed folders).
                    $first = Get-VMHardDiskDrive -VM $vm | Where-Object { $_.Path } | Select-Object -First 1
                    if ($first) { $root = Split-Path -Path $first.Path -Parent }
                }
                if (-not $a.newDisk.folder) { Assert-StorageFolder $root 'The folder of the new disk' }
                if ($a.newDisk.folder) {
                    # Folder chosen by the gateway (storage limits of the VM's folder) and validated by the agent.
                    $root = [string]$a.newDisk.folder
                    if (-not (Test-Path -LiteralPath $root)) {
                        try { New-Item -ItemType Directory -Path $root -Force | Out-Null }
                        catch { throw "Cannot create $root with the agent service account: $($_.Exception.Message)" }
                    }
                }
                $file = Join-Path $root ('{0}_{1}.vhdx' -f (Get-SafeName $vm.Name), (Get-Date -Format 'yyyyMMddHHmmss'))
                if (Test-Path -LiteralPath $file) { throw "File already exists: $file" }
                $size = [uint64]$a.newDisk.sizeGb * 1GB
                if ([bool]$a.newDisk.fixed) { New-VHD -Path $file -SizeBytes $size -Fixed | Out-Null } else { New-VHD -Path $file -SizeBytes $size -Dynamic | Out-Null }
                try { Add-VMHardDiskDrive -VM $vm -Path $file }
                catch { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue; throw }
                [void]$applied.Add("newDisk:$file")
            }

            if ($a.expandDisk) {
                $target = [string]$a.expandDisk.path
                $hd = Get-VMHardDiskDrive -VM $vm | Where-Object { $_.Path -and $_.Path.Equals($target, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
                if (-not $hd) { throw 'The disk is not attached to this VM.' }
                $vhd = Get-VHD -Path $hd.Path
                $newSize = [uint64]$a.expandDisk.newSizeGb * 1GB
                if ($newSize -le $vhd.Size) { throw 'The new size must be larger than the current size (shrinking is not supported).' }
                Resize-VHD -Path $hd.Path -SizeBytes $newSize
                [void]$applied.Add('expandDisk')
            }

            if ($a.newName) { Rename-VM -VM $vm -NewName ([string]$a.newName); [void]$applied.Add('rename') }
            Write-Response $true (Get-Config (Get-VmById $a.vmId)) $null $applied
        }

        'NewVm' {
            $name = [string]$a.name
            if (Get-VM -Name $name -ErrorAction SilentlyContinue) { throw "A VM named '$name' already exists on this host." }
            Test-IsoPath ([string]$a.isoPath) $a.isoRoots
            # From a template: its disks are copied into the folder of the new VM (the first one is the boot disk).
            $templateDisks = @(@($a.templateDisks) | Where-Object { $_ } | ForEach-Object { Get-TemplateDisk ([string]$_) $a.templateRoots })
            $copied = New-Object System.Collections.ArrayList
            $extraDisks = New-Object System.Collections.ArrayList
            $h = Get-VMHost
            $gen = [int16]$a.generation
            $params = @{ Name = $name; Generation = $gen; MemoryStartupBytes = [int64]$a.memoryStartupMb * 1MB }
            if ($a.switchName) {
                $sw = Get-VMSwitch -Name ([string]$a.switchName) -ErrorAction SilentlyContinue | Where-Object { $_.Name -ceq [string]$a.switchName } | Select-Object -First 1
                if (-not $sw) { throw "Virtual switch not found: $($a.switchName)" }
                $params.SwitchName = $sw.Name
            }
            $vhdFolder = $h.VirtualHardDiskPath
            $vmFolder = $null
            if (-not $a.location) {
                Assert-StorageFolder $h.VirtualMachinePath 'The default folder of the VMs of this host'
                Assert-StorageFolder $h.VirtualHardDiskPath 'The default folder of the virtual disks of this host'
            }
            if ($a.location) {
                # <location>\<name>\: Hyper-V puts the configuration in "Virtual Machines", the new disk goes to "Virtual Hard Disks".
                $location = [string]$a.location
                $vmFolder = Join-Path $location $name
                if ((Test-Path -LiteralPath $vmFolder) -and (Get-ChildItem -LiteralPath $vmFolder -Force | Select-Object -First 1)) {
                    throw "The folder $vmFolder already exists and is not empty."
                }
                $vhdFolder = Join-Path $vmFolder 'Virtual Hard Disks'
                try { New-Item -ItemType Directory -Path $vhdFolder -Force | Out-Null }
                catch { throw "Cannot create $vhdFolder with the agent service account: $($_.Exception.Message)" }
                [void]$applied.Add('folder')
                $params.Path = $location
            }
            $diskPath = $null
            if ($templateDisks.Count -gt 0) {
                if (-not (Test-Path -LiteralPath $vhdFolder)) { New-Item -ItemType Directory -Path $vhdFolder -Force | Out-Null }
                $need = [int64]0
                foreach ($t in $templateDisks) { $need += [int64]$t.Length }
                $volume = Get-PSDrive -Name ([IO.Path]::GetFullPath($vhdFolder).Substring(0, 1)) -ErrorAction SilentlyContinue
                if ($volume -and $null -ne $volume.Free -and [int64]$volume.Free -lt $need) {
                    throw "Not enough free space in $vhdFolder for the disks of the template ($([math]::Ceiling($need / 1GB)) GB needed)."
                }
                try {
                    for ($i = 0; $i -lt $templateDisks.Count; $i++) {
                        $suffix = if ($i -eq 0) { '' } else { "-disk$i" }
                        $dest = Join-Path $vhdFolder ((Get-SafeName $name) + $suffix + $templateDisks[$i].Extension.ToLowerInvariant())
                        if (Test-Path -LiteralPath $dest) { throw "File already exists: $dest" }
                        Copy-Item -LiteralPath $templateDisks[$i].FullName -Destination $dest
                        [void]$copied.Add($dest)
                        if ($i -eq 0) { $diskPath = $dest } else { [void]$extraDisks.Add($dest) }
                    }
                    # A larger boot disk can be asked for; the copy is never shrunk (the guest extends its own partition).
                    if ([int64]$a.diskSizeGb -gt 0) {
                        $wanted = [uint64]$a.diskSizeGb * 1GB
                        if ($wanted -gt [uint64](Get-VHD -Path $diskPath).Size) { Resize-VHD -Path $diskPath -SizeBytes $wanted }
                    }
                } catch {
                    foreach ($c in $copied) { Remove-Item -LiteralPath $c -Force -ErrorAction SilentlyContinue }
                    if ($vmFolder -and -not (Get-ChildItem -LiteralPath $vmFolder -Recurse -File -Force -ErrorAction SilentlyContinue | Select-Object -First 1)) {
                        Remove-Item -LiteralPath $vmFolder -Recurse -Force -ErrorAction SilentlyContinue
                    }
                    throw
                }
                [void]$applied.Add('template-disks')
                $params.VHDPath = $diskPath
            } elseif ([int64]$a.diskSizeGb -gt 0) {
                $diskPath = Join-Path $vhdFolder ((Get-SafeName $name) + '.vhdx')
                if (Test-Path -LiteralPath $diskPath) { throw "File already exists: $diskPath" }
                $size = [uint64]$a.diskSizeGb * 1GB
                if ([bool]$a.diskFixed) { New-VHD -Path $diskPath -SizeBytes $size -Fixed | Out-Null; $params.VHDPath = $diskPath }
                else { $params.NewVHDPath = $diskPath; $params.NewVHDSizeBytes = $size }
            } else {
                $params.NoVHD = $true
            }
            try { $vm = New-VM @params }
            catch {
                # Leave nothing behind: the fixed disk created above (the path did not exist) and the folders created above
                # if they are still empty (never pre-existing content).
                if ([bool]$a.diskFixed -and $diskPath) { Remove-Item -LiteralPath $diskPath -Force -ErrorAction SilentlyContinue }
                foreach ($c in $copied) { Remove-Item -LiteralPath $c -Force -ErrorAction SilentlyContinue }
                if ($vmFolder -and -not (Get-ChildItem -LiteralPath $vmFolder -Recurse -File -Force -ErrorAction SilentlyContinue | Select-Object -First 1)) {
                    Remove-Item -LiteralPath $vmFolder -Recurse -Force -ErrorAction SilentlyContinue
                }
                throw
            }
            [void]$applied.Add('created')
            $warning = $null
            $vlanFailed = $false
            if ($null -ne $a.vlanId) {
                # Protocol 9: VLAN of the portal network chosen in the wizard. On failure the adapter is disconnected and the
                # VM is not started, so it never reaches the untagged network of the switch.
                try {
                    $nic = Get-VMNetworkAdapter -VM $vm | Select-Object -First 1
                    Set-VMNetworkAdapterVlan -VMNetworkAdapter $nic -Access -VlanId ([int]$a.vlanId)
                    [void]$applied.Add("vlan:$([int]$a.vlanId)")
                } catch {
                    $vlanFailed = $true
                    Get-VMNetworkAdapter -VM $vm | Disconnect-VMNetworkAdapter -ErrorAction SilentlyContinue
                    $warning = "The VM was created but the VLAN of its network adapter could not be set ($($_.Exception.Message)): the adapter was disconnected and the VM was not started."
                }
            }
            try {
                Set-VMProcessor -VM $vm -Count ([int64]$a.processorCount); [void]$applied.Add('processor')
                if ([bool]$a.dynamicMemory) {
                    $mm = @{ DynamicMemoryEnabled = $true; StartupBytes = [int64]$a.memoryStartupMb * 1MB }
                    if ($a.memoryMinimumMb) { $mm.MinimumBytes = [int64]$a.memoryMinimumMb * 1MB }
                    if ($a.memoryMaximumMb) { $mm.MaximumBytes = [int64]$a.memoryMaximumMb * 1MB }
                    Set-VMMemory -VM $vm @mm
                } else {
                    Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
                }
                [void]$applied.Add('memory')
                foreach ($extra in $extraDisks) { Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -Path $extra; [void]$applied.Add('disk') }
                if ($a.notes) { Set-VM -VM $vm -Notes ([string]$a.notes) }
                if ($a.checkpointType) { Set-VM -VM $vm -CheckpointType ([string]$a.checkpointType) }
                if ($a.automaticStartAction) { Set-VM -VM $vm -AutomaticStartAction ([string]$a.automaticStartAction) }
                if ($a.isoPath) {
                    $dvd = Get-VMDvdDrive -VM $vm | Select-Object -First 1
                    if ($dvd) { Set-VMDvdDrive -VMDvdDrive $dvd -Path ([string]$a.isoPath) }
                    else { $dvd = Add-VMDvdDrive -VM $vm -Path ([string]$a.isoPath) -Passthru }
                    if ($gen -eq 2) { Set-VMFirmware -VM $vm -FirstBootDevice $dvd }
                    [void]$applied.Add('iso')
                }
                if ($gen -eq 2) {
                    $tpl = [string]$a.secureBootTemplate
                    if ($tpl -eq 'Off') { Set-VMFirmware -VM $vm -EnableSecureBoot Off }
                    elseif ($tpl) { Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate $tpl }
                    [void]$applied.Add('firmware')
                    if ([bool]$a.enableTpm) {
                        Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
                        Enable-VMTPM -VM $vm
                        [void]$applied.Add('tpm')
                    }
                }
                if ([bool]$a.startAfterCreate -and -not $vlanFailed) { Start-VM -VM $vm; [void]$applied.Add('started') }
            } catch {
                $warning = "The VM was created but its configuration is incomplete: $($_.Exception.Message)" + $(if ($warning) { " $warning" } else { '' })
            }
            Write-Response $true @{ vmId = "$($vm.Id)"; name = $vm.Name; configurationLocation = $vm.ConfigurationLocation; diskPath = $diskPath; warning = $warning } $null $applied
        }

        'ListIsos' {
            $files = New-Object System.Collections.ArrayList
            foreach ($r in @($a.roots)) {
                if ([string]::IsNullOrWhiteSpace($r)) { continue }
                $root = [string]$r
                $item = Get-Item -LiteralPath $root -ErrorAction SilentlyContinue
                if (-not $item -or -not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
                Get-ChildItem -LiteralPath $root -Filter '*.iso' -File -Recurse -Depth 2 -ErrorAction SilentlyContinue |
                    Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
                    Select-Object -First 500 |
                    ForEach-Object { [void]$files.Add(@{ path = $_.FullName; sizeBytes = [int64]$_.Length; modifiedUtc = $_.LastWriteTimeUtc.ToString('o') }) }
            }
            Write-Response $true @($files) $null @()
        }

        'ListCheckpoints' {
            $vm = Get-VmById $a.vmId
            $current = "$($vm.ParentSnapshotId)"
            $list = @(Get-VMSnapshot -VM $vm | ForEach-Object {
                @{ id = "$($_.Id)"; name = $_.Name; parentId = $(if ($_.ParentSnapshotId) { "$($_.ParentSnapshotId)" } else { $null })
                   createdUtc = $_.CreationTime.ToUniversalTime().ToString('o'); type = "$($_.SnapshotType)"; isCurrent = ("$($_.Id)" -eq $current) }
            })
            Write-Response $true $list $null @()
        }

        'NewCheckpoint' {
            $vm = Get-VmById $a.vmId
            $s = Checkpoint-VM -VM $vm -SnapshotName ([string]$a.name) -Passthru
            Write-Response $true @{ id = "$($s.Id)"; name = $s.Name } $null @('created')
        }

        'RestoreCheckpoint' {
            $vm = Get-VmById $a.vmId
            $s = Get-VMSnapshot -VM $vm | Where-Object { "$($_.Id)" -eq [string]$a.checkpointId } | Select-Object -First 1
            if (-not $s) { throw 'Checkpoint not found.' }
            # Applying discards the current state anyway: a running or paused VM is turned off first, so the result does
            # not depend on how Restore-VMSnapshot treats a VM that is not off.
            if ("$($vm.State)" -notin @('Off', 'Saved')) {
                Stop-VM -VM $vm -TurnOff -Force -Confirm:$false
                [void]$applied.Add('turnedOff')
            }
            Restore-VMSnapshot -VMSnapshot $s -Confirm:$false
            [void]$applied.Add('restored')
            $after = Get-VmById $a.vmId
            [void]$applied.Add("state:$($after.State)")
            Write-Response $true @{ id = "$($s.Id)"; state = "$($after.State)" } $null $applied
        }

        'RemoveCheckpoint' {
            $vm = Get-VmById $a.vmId
            $s = Get-VMSnapshot -VM $vm | Where-Object { "$($_.Id)" -eq [string]$a.checkpointId } | Select-Object -First 1
            if (-not $s) { throw 'Checkpoint not found.' }
            if ([bool]$a.includeChildren) { Remove-VMSnapshot -VMSnapshot $s -IncludeAllChildSnapshots -Confirm:$false }
            else { Remove-VMSnapshot -VMSnapshot $s -Confirm:$false }
            Write-Response $true @{ id = "$($s.Id)" } $null @('removed')
        }

        'ListSwitches' {
            $byswitch = @{}
            Get-VM | ForEach-Object {
                $vmName = $_.Name
                Get-VMNetworkAdapter -VM $_ | Where-Object { $_.SwitchName } | ForEach-Object {
                    if (-not $byswitch.ContainsKey($_.SwitchName)) { $byswitch[$_.SwitchName] = New-Object System.Collections.ArrayList }
                    [void]$byswitch[$_.SwitchName].Add($vmName)
                }
            }
            $list = @(Get-VMSwitch | ForEach-Object {
                $vms = @()
                if ($byswitch.ContainsKey($_.Name)) { $vms = @($byswitch[$_.Name] | Sort-Object -Unique) }
                @{ id = "$($_.Id)"; name = $_.Name; type = "$($_.SwitchType)"; netAdapter = $_.NetAdapterInterfaceDescription
                   allowManagementOs = [bool]$_.AllowManagementOS; embeddedTeaming = [bool]$_.EmbeddedTeamingEnabled; connectedVms = $vms }
            })
            Write-Response $true $list $null @()
        }

        'ListNetAdapters' {
            # Physical adapters and the virtual switch each one is bound to (a SET switch lists several adapters).
            $bound = @{}
            foreach ($sw in @(Get-VMSwitch)) {
                $descriptions = @($sw.NetAdapterInterfaceDescriptions | Where-Object { $_ })
                if ($descriptions.Count -eq 0 -and $sw.NetAdapterInterfaceDescription) { $descriptions = @($sw.NetAdapterInterfaceDescription) }
                foreach ($d in $descriptions) { $bound[[string]$d] = $sw.Name }
            }
            $list = @(Get-NetAdapter -Physical | Sort-Object Name | ForEach-Object {
                $sw = $null
                if ($bound.ContainsKey([string]$_.InterfaceDescription)) { $sw = $bound[[string]$_.InterfaceDescription] }
                @{ name = $_.Name; description = $_.InterfaceDescription; status = "$($_.Status)"; linkSpeed = "$($_.LinkSpeed)"
                   macAddress = $_.MacAddress; switchName = $sw }
            })
            Write-Response $true $list $null @()
        }

        'NewSwitch' {
            $name = [string]$a.name
            $type = [string]$a.type
            # Switch names are not unique in Hyper-V, but VMs are connected by name: refuse duplicates.
            if (Get-VMSwitch | Where-Object { $_.Name -eq $name }) { throw "A virtual switch named '$name' already exists on this host." }
            $params = @{ Name = $name }
            if ($a.notes) { $params.Notes = [string]$a.notes }
            if ($type -eq 'External') {
                $adapter = Get-NetAdapter -Physical | Where-Object { $_.Name -ceq [string]$a.netAdapterName } | Select-Object -First 1
                if (-not $adapter) { throw "Physical network adapter not found: $($a.netAdapterName)" }
                $params.NetAdapterName = $adapter.Name
                $params.AllowManagementOS = [bool]$a.allowManagementOs
            } elseif ($type -eq 'Internal') {
                $params.SwitchType = 'Internal'
            } elseif ($type -eq 'Private') {
                $params.SwitchType = 'Private'
            } else {
                throw 'Unsupported switch type.'
            }
            $sw = New-VMSwitch @params
            Write-Response $true @{ id = "$($sw.Id)"; name = $sw.Name; type = "$($sw.SwitchType)" } $null @('created')
        }

        'RemoveSwitch' {
            $id = [guid][string]$a.switchId
            $sw = Get-VMSwitch | Where-Object { $_.Id -eq $id } | Select-Object -First 1
            if (-not $sw) { throw 'Virtual switch not found.' }
            $used = @(Get-VM | ForEach-Object { $v = $_; Get-VMNetworkAdapter -VM $v | Where-Object { $_.SwitchId -eq $id } | ForEach-Object { $v.Name } } | Sort-Object -Unique)
            if ($used.Count -gt 0) { throw "The switch is used by: $(($used | Select-Object -First 10) -join ', '). Disconnect the VMs first." }
            Remove-VMSwitch -VMSwitch $sw -Force -Confirm:$false
            Write-Response $true @{ id = "$id"; name = $sw.Name } $null @('removed')
        }

        'NewTemplate' {
            # A template is a copy of the disks of a VM that is off, plus the hardware the gateway records. The VM is not changed.
            $vm = Get-VmById $a.vmId
            if ("$($vm.State)" -ne 'Off') { throw "The VM must be off to create a template from it (it is $($vm.State))." }
            if (Get-VMSnapshot -VM $vm -ErrorAction SilentlyContinue | Select-Object -First 1) {
                throw 'The VM has checkpoints: delete them (Hyper-V merges the disks) before creating a template.'
            }
            $folder = Get-TemplateFolder ([string]$a.folder) $a.roots -CreateLibrary
            if (Test-Path -LiteralPath $folder) { throw "The template folder already exists: $folder" }
            $sources = New-Object System.Collections.ArrayList
            $need = [int64]0
            foreach ($d in @(Get-VMHardDiskDrive -VM $vm)) {
                if ([string]::IsNullOrEmpty($d.Path) -or $d.DiskNumber) { throw 'The VM uses a physical (pass-through) disk: it cannot become a template.' }
                $ext = [IO.Path]::GetExtension($d.Path).ToLowerInvariant()
                if ($ext -ne '.vhdx' -and $ext -ne '.vhd') { throw "The disk $($d.Path) is not a .vhdx or .vhd file: it cannot be copied into a template." }
                $vhd = Get-VHD -Path $d.Path
                if ($vhd.ParentPath) { throw "The disk $($d.Path) is a differencing disk: merge it before creating a template." }
                $file = Get-Item -LiteralPath $d.Path
                $need += [int64]$file.Length
                [void]$sources.Add(@{ path = $d.Path; ext = $ext; size = [int64]$vhd.Size })
            }
            if ($sources.Count -eq 0) { throw 'The VM has no virtual disk: there is nothing to put in a template.' }
            $volume = Get-PSDrive -Name $folder.Substring(0, 1) -ErrorAction SilentlyContinue
            if ($volume -and $null -ne $volume.Free -and [int64]$volume.Free -lt $need) {
                throw "Not enough free space in the template library for the disks of this VM ($([math]::Ceiling($need / 1GB)) GB needed)."
            }
            try { New-Item -ItemType Directory -Path $folder | Out-Null }
            catch { throw "Cannot create $folder with the agent service account: $($_.Exception.Message)" }
            [void]$applied.Add('folder')
            $disks = New-Object System.Collections.ArrayList
            try {
                for ($i = 0; $i -lt $sources.Count; $i++) {
                    $name = "disk$i$($sources[$i].ext)"
                    $dest = Join-Path $folder $name
                    Copy-Item -LiteralPath $sources[$i].path -Destination $dest
                    [void]$disks.Add(@{ file = $name; sizeBytes = [int64]$sources[$i].size; fileBytes = [int64](Get-Item -LiteralPath $dest).Length })
                }
            } catch {
                # Leave nothing behind: the folder was created by this operation.
                Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
                throw
            }
            [void]$applied.Add('copied')
            $cfg = Get-Config $vm
            $sb = $null
            if ($cfg.generation -eq 2) { $sb = if ($cfg.secureBootEnabled) { $cfg.secureBootTemplate } else { 'Off' } }
            Write-Response $true @{
                folder = $folder; disks = @($disks); generation = [int]$cfg.generation; processorCount = [int]$cfg.processorCount
                memoryStartupMb = [int64]($cfg.memoryStartupBytes / 1MB); dynamicMemory = [bool]$cfg.dynamicMemoryEnabled
                memoryMinimumMb = [int64]($cfg.memoryMinimumBytes / 1MB); memoryMaximumMb = [int64]($cfg.memoryMaximumBytes / 1MB)
                secureBootTemplate = $sb; tpmEnabled = [bool]$cfg.tpmEnabled
            } $null $applied
        }

        'RemoveTemplate' {
            $folder = Get-TemplateFolder ([string]$a.folder) $a.roots
            if (-not (Test-Path -LiteralPath $folder)) {
                Write-Response $true @{ removed = $false; folder = $folder } $null $applied
            } else {
                Assert-NoReparsePoint (Get-Item -LiteralPath $folder -Force)
                $entries = @(Get-ChildItem -LiteralPath $folder -Force)
                foreach ($e in $entries) {
                    if ($e.PSIsContainer -or ($e.Extension -ine '.vhdx' -and $e.Extension -ine '.vhd')) {
                        throw "The template folder contains something that is not a template disk ($($e.Name)): nothing was deleted."
                    }
                }
                foreach ($e in $entries) { Remove-Item -LiteralPath $e.FullName -Force }
                Remove-Item -LiteralPath $folder -Force
                [void]$applied.Add('removed')
                Write-Response $true @{ removed = $true; folder = $folder } $null $applied
            }
        }

        'ListHostDisks' {
            $list = New-Object System.Collections.ArrayList
            Get-VM | ForEach-Object {
                $v = $_
                Get-VMHardDiskDrive -VM $v | ForEach-Object {
                    $vhd = $null
                    if ($_.Path) { $vhd = Get-VHD -Path $_.Path -ErrorAction SilentlyContinue }
                    [void]$list.Add(@{
                        path = $_.Path; vmId = "$($v.Id)"; vmName = $v.Name; controller = ('{0} {1}:{2}' -f $_.ControllerType, $_.ControllerNumber, $_.ControllerLocation)
                        format = $(if ($vhd) { "$($vhd.VhdFormat)" } else { $null }); diskType = $(if ($vhd) { "$($vhd.VhdType)" } else { $null })
                        sizeBytes = $(if ($vhd) { [int64]$vhd.Size } else { $null }); fileSizeBytes = $(if ($vhd) { [int64]$vhd.FileSize } else { $null })
                        parentPath = $(if ($vhd -and $vhd.ParentPath) { $vhd.ParentPath } else { $null })
                    })
                }
            }
            Write-Response $true @($list) $null @()
        }

        'ListVmGroups' {
            Write-Response $true @(Get-VMGroup | ForEach-Object { ConvertTo-GroupInfo $_ }) $null @()
        }

        'SyncVmGroups' {
            # Only groups whose id is in $a.managed (created or imported by the portal) are changed or removed.
            $errors = New-Object System.Collections.ArrayList
            $removed = New-Object System.Collections.ArrayList
            $ids = @{}
            $managed = @{}
            foreach ($m in @($a.managed)) { if ($m) { $managed[([string]$m).ToLowerInvariant()] = $true } }
            $existing = @{}
            foreach ($g in @(Get-VMGroup)) { $existing[(Get-GroupId $g).ToLowerInvariant()] = $g }
            $vms = @{}
            foreach ($v in @(Get-VM)) { $vms["$($v.Id)".ToLowerInvariant()] = $v }
            $items = @($a.items | Where-Object { $_ })

            # 1. Every desired group exists with the desired name.
            foreach ($it in $items) {
                $want = [string]$it.id
                if ($want) { $want = $want.ToLowerInvariant() }
                try {
                    $g = $null
                    if ($want -and $managed.ContainsKey($want) -and $existing.ContainsKey($want)) { $g = $existing[$want] }
                    if ($g -and "$($g.GroupType)" -ne [string]$it.groupType) { $g = $null }
                    if (-not $g) {
                        $newId = [guid]::NewGuid()
                        $g = New-VMGroup -Name ([string]$it.name) -GroupType ([string]$it.groupType) -Id $newId
                        $gid = "$newId".ToLowerInvariant()
                        $existing[$gid] = $g
                        $managed[$gid] = $true
                        [void]$applied.Add("created:$($it.name)")
                    } else {
                        $gid = (Get-GroupId $g).ToLowerInvariant()
                        if ($g.Name -cne [string]$it.name) { Rename-VMGroup -VMGroup $g -NewName ([string]$it.name); [void]$applied.Add("renamed:$($it.name)") }
                    }
                    $ids[[string]$it.key] = $gid
                } catch {
                    [void]$errors.Add("$($it.name): $($_.Exception.Message)")
                    # Never treat an existing group as "no longer desired" because an update failed.
                    if ($want -and $existing.ContainsKey($want)) { $ids[[string]$it.key] = $want }
                }
            }

            # 2. Members: VMs of VM collections, VM collections of management collections.
            foreach ($it in $items) {
                $gid = $ids[[string]$it.key]
                if (-not $gid) { continue }
                try {
                    $g = Get-VMGroup -Id ([guid]$gid)
                    if ([string]$it.groupType -eq 'VMCollectionType') {
                        $want = @{}
                        foreach ($v in @($it.vmIds)) { if ($v) { $want[([string]$v).ToLowerInvariant()] = $true } }
                        $have = @{}
                        foreach ($m in @($g.VMMembers | Where-Object { $_ })) { $have["$($m.Id)".ToLowerInvariant()] = $m }
                        foreach ($k in @($want.Keys)) {
                            if (-not $have.ContainsKey($k) -and $vms.ContainsKey($k)) { Add-VMGroupMember -VMGroup $g -VM $vms[$k]; [void]$applied.Add("vm+:$($it.name)") }
                        }
                        foreach ($k in @($have.Keys)) {
                            if (-not $want.ContainsKey($k)) { Remove-VMGroupMember -VMGroup $g -VM $have[$k]; [void]$applied.Add("vm-:$($it.name)") }
                        }
                    } else {
                        $want = @{}
                        foreach ($mk in @($it.memberKeys)) { if ($mk -and $ids[[string]$mk]) { $want[$ids[[string]$mk]] = $true } }
                        $have = @{}
                        foreach ($m in @($g.VMGroupMembers | Where-Object { $_ })) { $have[(Get-GroupId $m).ToLowerInvariant()] = $m }
                        foreach ($k in @($want.Keys)) {
                            if (-not $have.ContainsKey($k)) { Add-VMGroupMember -VMGroup $g -VMGroupMember (Get-VMGroup -Id ([guid]$k)); [void]$applied.Add("group+:$($it.name)") }
                        }
                        foreach ($k in @($have.Keys)) {
                            if (-not $want.ContainsKey($k)) { Remove-VMGroupMember -VMGroup $g -VMGroupMember $have[$k]; [void]$applied.Add("group-:$($it.name)") }
                        }
                    }
                } catch { [void]$errors.Add("$($it.name): $($_.Exception.Message)") }
            }

            # 3. Managed groups no longer desired: detach from managed parents, empty, remove (Remove-VMGroup needs an empty group).
            $keep = @{}
            foreach ($v in @($ids.Values)) { $keep[[string]$v] = $true }
            $toRemove = @($managed.Keys | Where-Object { -not $keep.ContainsKey($_) })
            foreach ($rid in $toRemove) {
                if (-not $existing.ContainsKey($rid)) { [void]$removed.Add($rid); continue }
                try {
                    foreach ($p in @(Get-VMGroup | Where-Object { "$($_.GroupType)" -eq 'ManagementCollectionType' })) {
                        $parentId = (Get-GroupId $p).ToLowerInvariant()
                        if (-not $managed.ContainsKey($parentId)) { continue }
                        foreach ($m in @($p.VMGroupMembers | Where-Object { $_ })) {
                            if ((Get-GroupId $m).ToLowerInvariant() -eq $rid) { Remove-VMGroupMember -VMGroup $p -VMGroupMember $m }
                        }
                    }
                    $g = Get-VMGroup -Id ([guid]$rid)
                    foreach ($m in @($g.VMMembers | Where-Object { $_ })) { Remove-VMGroupMember -VMGroup $g -VM $m }
                    foreach ($m in @($g.VMGroupMembers | Where-Object { $_ })) { Remove-VMGroupMember -VMGroup $g -VMGroupMember $m }
                    Remove-VMGroup -VMGroup (Get-VMGroup -Id ([guid]$rid)) -Force
                    [void]$removed.Add($rid)
                    [void]$applied.Add("removed:$($g.Name)")
                } catch { [void]$errors.Add("remove $($rid): $($_.Exception.Message)") }
            }
            Write-Response $true @{ ids = $ids; removed = @($removed); errors = @($errors) } $null $applied
        }

        'GetHostHealth' {
            # Read-only. Each section is read on its own: a section that fails is reported in errors, never guessed.
            $errors = New-Object System.Collections.ArrayList
            $hardware = $null
            try {
                $cs = Get-CimInstance Win32_ComputerSystem
                $bios = Get-CimInstance Win32_BIOS
                $modules = @(Get-CimInstance Win32_PhysicalMemory)
                $cpus = @(Get-CimInstance Win32_Processor)
                $bad = @(@($cs.Status) + @($cpus | ForEach-Object { $_.Status }) | Where-Object { $_ -and $_ -ne 'OK' })
                $biosDate = $null
                if ($bios.ReleaseDate) { $biosDate = ([datetime]$bios.ReleaseDate).ToUniversalTime().ToString('o') }
                $hardware = @{
                    manufacturer = "$($cs.Manufacturer)".Trim(); model = "$($cs.Model)".Trim(); serialNumber = "$($bios.SerialNumber)".Trim()
                    biosVersion = "$($bios.SMBIOSBIOSVersion)".Trim(); biosDate = $biosDate; memoryModules = $modules.Count
                    memoryInstalledBytes = [int64](($modules | Measure-Object -Property Capacity -Sum).Sum)
                    processors = @($cpus | ForEach-Object { "$($_.Name)".Trim() })
                    status = $(if ($bad.Count -gt 0) { [string]$bad[0] } else { 'OK' })
                }
            } catch { [void]$errors.Add("hardware: $($_.Exception.Message)") }

            $disks = New-Object System.Collections.ArrayList
            try {
                foreach ($d in @(Get-PhysicalDisk -ErrorAction Stop)) {
                    $rel = $null
                    try { $rel = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch { $rel = $null }
                    $health = ConvertTo-HealthName $d.HealthStatus
                    $ops = @($d.OperationalStatus | ForEach-Object { ConvertTo-OperationalName $_ })
                    $status = Get-StatusFromHealth $health
                    $wear = $null; $temp = $null; $readErr = $null; $writeErr = $null; $hours = $null
                    if ($rel) {
                        if ($null -ne $rel.Wear) { $wear = [int]$rel.Wear }
                        if ($rel.Temperature) { $temp = [int]$rel.Temperature }
                        if ($null -ne $rel.ReadErrorsUncorrected) { $readErr = [int64]$rel.ReadErrorsUncorrected }
                        if ($null -ne $rel.WriteErrorsUncorrected) { $writeErr = [int64]$rel.WriteErrorsUncorrected }
                        if ($null -ne $rel.PowerOnHours) { $hours = [int64]$rel.PowerOnHours }
                    }
                    if ($status -eq 'Ok' -and (($wear -ge 90) -or ($readErr -gt 0) -or ($writeErr -gt 0) -or ($ops | Where-Object { $_ -match 'Predictive|Degraded|Error|Failed|Lost' }))) { $status = 'Warning' }
                    [void]$disks.Add(@{
                        name = "$($d.FriendlyName)"; serialNumber = "$($d.SerialNumber)".Trim(); mediaType = "$($d.MediaType)"; busType = "$($d.BusType)"
                        sizeBytes = [int64]$d.Size; healthStatus = $health; operationalStatus = $ops; temperatureCelsius = $temp; wearPercent = $wear
                        readErrorsUncorrected = $readErr; writeErrorsUncorrected = $writeErr; powerOnHours = $hours; status = $status
                    })
                }
            } catch { [void]$errors.Add("physicalDisks: $($_.Exception.Message)") }

            $volumes = New-Object System.Collections.ArrayList
            try {
                foreach ($v in @(Get-Volume -ErrorAction Stop | Where-Object { ("$($_.DriveType)" -eq 'Fixed' -or "$($_.DriveType)" -eq '3') -and $_.Size -gt 0 })) {
                    $health = ConvertTo-HealthName $v.HealthStatus
                    $status = Get-StatusFromHealth $health
                    $free = [int64]$v.SizeRemaining; $size = [int64]$v.Size
                    if ($status -eq 'Ok' -and $size -gt 0) {
                        if ($free / $size -lt 0.05) { $status = 'Critical' } elseif ($free / $size -lt 0.10) { $status = 'Warning' }
                    }
                    $name = $(if ($v.DriveLetter) { "$($v.DriveLetter):" } elseif ($v.FileSystemLabel) { "$($v.FileSystemLabel)" } else { 'Volume' })
                    [void]$volumes.Add(@{
                        name = $name; label = "$($v.FileSystemLabel)"; fileSystem = "$($v.FileSystem)"; sizeBytes = $size; freeBytes = $free
                        healthStatus = $health; operationalStatus = (@($v.OperationalStatus | ForEach-Object { ConvertTo-OperationalName $_ }) -join ', '); status = $status
                    })
                }
            } catch { [void]$errors.Add("volumes: $($_.Exception.Message)") }

            $pools = New-Object System.Collections.ArrayList
            try {
                foreach ($p in @(Get-StoragePool -IsPrimordial $false -ErrorAction Stop)) {
                    $health = ConvertTo-HealthName $p.HealthStatus
                    $vds = @(Get-VirtualDisk -StoragePool $p -ErrorAction SilentlyContinue | ForEach-Object {
                        "$($_.FriendlyName): $(ConvertTo-HealthName $_.HealthStatus), $((@($_.OperationalStatus | ForEach-Object { ConvertTo-OperationalName $_ })) -join ' ')"
                    })
                    $status = Get-StatusFromHealth $health
                    if ($status -eq 'Ok' -and ($vds | Where-Object { $_ -notmatch ': Healthy, OK' })) { $status = 'Warning' }
                    [void]$pools.Add(@{
                        name = "$($p.FriendlyName)"; healthStatus = $health; operationalStatus = (@($p.OperationalStatus | ForEach-Object { ConvertTo-OperationalName $_ }) -join ', ')
                        sizeBytes = [int64]$p.Size; allocatedBytes = [int64]$p.AllocatedSize; virtualDisks = $vds; status = $status
                    })
                }
            } catch { [void]$errors.Add("storagePools: $($_.Exception.Message)") }

            $adapters = New-Object System.Collections.ArrayList
            try {
                foreach ($n in @(Get-NetAdapter -Physical -ErrorAction Stop | Sort-Object Name)) {
                    $st = "$($n.Status)"
                    [void]$adapters.Add(@{ name = $n.Name; description = $n.InterfaceDescription; status = $st; linkSpeed = "$($n.LinkSpeed)"
                        health = $(if ($st -eq 'Up' -or $st -eq 'Disabled') { 'Ok' } else { 'Warning' }) })
                }
            } catch { [void]$errors.Add("networkAdapters: $($_.Exception.Message)") }

            $events = New-Object System.Collections.ArrayList
            $providers = @('disk', 'Ntfs', 'Microsoft-Windows-Ntfs', 'volmgr', 'volsnap', 'storahci', 'stornvme', 'storport', 'iaStorA', 'iaStorAVC',
                'megasas', 'megasas2i', 'percsas', 'percsas2i', 'percsas3i', 'LSI_SAS', 'LSI_SAS3', 'HpCISSs2', 'HpCISSs3', 'smartpqi', 'mpt3sas',
                'Microsoft-Windows-WHEA-Logger', 'Microsoft-Windows-Kernel-WHEA', 'Microsoft-Windows-StorageSpaces-Driver', 'Microsoft-Windows-Kernel-Power',
                'Microsoft-Windows-Hyper-V-Hypervisor', 'Microsoft-Windows-Hyper-V-VmSwitch')
            $since = (Get-Date).AddDays(-7)
            foreach ($q in @(@{ LogName = 'System'; Level = 1, 2; StartTime = $since }, @{ LogName = 'Microsoft-Windows-Hyper-V-VMMS-Admin'; Level = 1, 2; StartTime = $since })) {
                try {
                    $found = @(Get-WinEvent -FilterHashtable $q -MaxEvents 400 -ErrorAction Stop)
                    if ($q.LogName -eq 'System') { $found = @($found | Where-Object { $providers -contains $_.ProviderName }) }
                    foreach ($e in @($found | Select-Object -First 40)) {
                        $msg = "$($e.Message)"
                        if ($msg.Length -gt 400) { $msg = $msg.Substring(0, 400) }
                        [void]$events.Add(@{ timeUtc = $e.TimeCreated.ToUniversalTime().ToString('o'); provider = $e.ProviderName; id = [int]$e.Id
                            level = $(if ($e.Level -eq 1) { 'Critical' } else { 'Error' }); message = $msg })
                    }
                } catch {
                    # "No events were found" is not an error.
                    if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { [void]$errors.Add("events ($($q.LogName)): $($_.Exception.Message)") }
                }
            }

            $services = @(foreach ($s in 'vmms', 'vmcompute', 'HvHost') {
                $svc = Get-Service -Name $s -ErrorAction SilentlyContinue
                if ($svc) { @{ name = $svc.Name; displayName = $svc.DisplayName; state = "$($svc.Status)"; startMode = "$($svc.StartType)" } }
            })
            Write-Response $true @{
                collectedUtc = (Get-Date).ToUniversalTime().ToString('o'); hardware = $hardware; physicalDisks = @($disks); volumes = @($volumes)
                storagePools = @($pools); networkAdapters = @($adapters); events = @($events); services = $services; errors = @($errors)
            } $null @()
        }

        'MoveVm' {
            # Live migration (running VM) or move of a VM that is off, to another host. Host migration settings are not changed here.
            $vm = Get-VmById $a.vmId
            $destination = [string]$a.destinationHost
            $params = @{ VM = $vm; DestinationHost = $destination }
            $path = $null
            if ([bool]$a.includeStorage) {
                $path = Join-Path ([string]$a.destinationPath) (Get-VmFolderName $vm.Name)
                $params.IncludeStorage = $true
                $params.DestinationStoragePath = $path
            }
            $state = "$($vm.State)"
            try { Move-VM @params }
            catch { throw "Move to $destination failed: $($_.Exception.Message) (live migration must be enabled on both hosts: Enable-VMMigration, authentication and networks; see Guide > Move VMs)." }
            [void]$applied.Add('moved')
            Write-Response $true @{ vmId = "$($vm.Id)"; name = $vm.Name; destinationHost = $destination; path = $path; state = $state } $null $applied
        }

        'MoveVmStorage' {
            $vm = Get-VmById $a.vmId
            $path = Join-Path ([string]$a.destinationPath) (Get-VmFolderName $vm.Name)
            if ((Test-Path -LiteralPath $path) -and (Get-ChildItem -LiteralPath $path -Force | Select-Object -First 1)) { throw "The folder $path already exists and is not empty." }
            try { Move-VMStorage -VM $vm -DestinationStoragePath $path }
            catch { throw "Moving the files of the VM failed: $($_.Exception.Message)" }
            [void]$applied.Add('moved')
            $after = Get-VmById $a.vmId
            Write-Response $true @{ vmId = "$($vm.Id)"; name = $vm.Name; destinationHost = $null; path = $after.Path; state = "$($after.State)" } $null $applied
        }

        'GetBackupReadiness' {
            # What backups need on this host. The Hyper-V module is already loaded (the script stops earlier without it).
            $checks = New-Object System.Collections.ArrayList
            [void]$checks.Add(@{ key = 'hyperv-module'; status = 'Ok'; message = 'Hyper-V PowerShell module available (checkpoints, export, import).'; fix = $null })
            foreach ($cmd in 'Export-VMSnapshot', 'Import-VM', 'Mount-VHD') {
                if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
                    [void]$checks.Add(@{ key = "cmdlet-$cmd"; status = 'Critical'; message = "$cmd is not available on this host."; fix = 'Install-WindowsFeature Hyper-V-PowerShell' })
                }
            }
            foreach ($id in @($a.vmIds)) {
                if (-not $id) { continue }
                $v = Get-VM -Id ([guid][string]$id) -ErrorAction SilentlyContinue
                if (-not $v) { continue }
                if ("$($v.CheckpointType)" -eq 'Disabled') {
                    [void]$checks.Add(@{ key = "vm-checkpoints:$($v.Id)"; status = 'Critical'; message = "Checkpoints are disabled on $($v.Name): its backup will fail."; fix = "Set-VM -Id $($v.Id) -CheckpointType Production" })
                    continue
                }
                # Backup (volume shadow copy) integration service, matched by its id (its name is localized; 84EAAE65-... is the heartbeat).
                $vss = Get-VMIntegrationService -VM $v | Where-Object { "$($_.Id)" -like '*5CED1297-4598-4915-A5FC-AD21BB4D02A4*' } | Select-Object -First 1
                if ($vss -and -not $vss.Enabled) {
                    [void]$checks.Add(@{ key = "vm-vss:$($v.Id)"; status = 'Warning'; message = "The backup integration service is disabled on $($v.Name): production checkpoints fall back to standard ones (crash-consistent copy)."
                        fix = "Get-VMIntegrationService -VMName '$($v.Name)' | Where-Object Id -like '*5CED1297-4598-4915-A5FC-AD21BB4D02A4*' | Enable-VMIntegrationService" })
                } elseif ("$($v.State)" -eq 'Running' -and "$($v.Heartbeat)" -notlike 'Ok*') {
                    [void]$checks.Add(@{ key = "vm-guest:$($v.Id)"; status = 'Warning'; message = "$($v.Name) does not answer through the integration services (heartbeat $($v.Heartbeat)): its copy may be only crash-consistent."; fix = 'Install or update the integration services in the guest.' })
                }
            }
            if ($a.target) {
                $repo = $null
                try {
                    $repo = Open-BackupTarget $a.target
                    $probe = Join-Path $repo.root ('.hvportal-write-test-' + [guid]::NewGuid().ToString('N'))
                    Set-Content -LiteralPath $probe -Value 'HvPortal write test' -Encoding ASCII
                    Remove-Item -LiteralPath $probe -Force
                    $how = $(if ($repo.drive) { 'with the stored credentials' } elseif ($repo.share) { 'with the computer account of this host' } else { 'with the agent service account' })
                    [void]$checks.Add(@{ key = 'repository'; status = 'Ok'; message = "Repository $([string]$a.target.path) reachable and writable $how."; fix = $null })
                    if (-not $repo.share) {
                        $drive = Get-PSDrive -Name ([string]$a.target.path).Substring(0, 1) -ErrorAction SilentlyContinue
                        if ($drive -and $null -ne $drive.Free) {
                            $gb = [math]::Round([int64]$drive.Free / 1GB)
                            [void]$checks.Add(@{ key = 'repository-space'; status = $(if ($gb -lt 50) { 'Warning' } else { 'Ok' }); message = "Free space on the repository drive: $gb GB."; fix = $null })
                        }
                    }
                } catch {
                    $fix = $(if ($a.target.userName) { 'Check the share path, user name and password of the repository (Backup > Repositories).' } else { 'Give the computer account of this host (HOST$) write access to the share, or use credentials.' })
                    [void]$checks.Add(@{ key = 'repository'; status = 'Critical'; message = $_.Exception.Message; fix = $fix })
                } finally { Close-BackupTarget $repo }
                if ($a.target.stagingPath) {
                    $staging = [string]$a.target.stagingPath
                    try {
                        if (-not (Test-Path -LiteralPath $staging)) { New-Item -ItemType Directory -Path $staging -Force | Out-Null }
                        $drive = Get-PSDrive -Name $staging.Substring(0, 1) -ErrorAction SilentlyContinue
                        $gb = $(if ($drive -and $null -ne $drive.Free) { [math]::Round([int64]$drive.Free / 1GB) } else { -1 })
                        [void]$checks.Add(@{ key = 'staging'; status = $(if ($gb -ge 0 -and $gb -lt 100) { 'Warning' } else { 'Ok' })
                            message = "Staging folder $staging available$(if ($gb -ge 0) { ", $gb GB free (it must hold a full copy of the largest VM)" })."; fix = $null })
                    } catch {
                        [void]$checks.Add(@{ key = 'staging'; status = 'Critical'; message = "Staging folder ${staging}: $($_.Exception.Message)"; fix = 'Choose a local folder the agent service account can write to.' })
                    }
                }
            }
            Write-Response $true @{ checks = @($checks) } $null @()
        }

        'BackupVm' {
            $vm = Get-VmById $a.vmId
            $pointId = [guid][string]$a.pointId
            $folder = Assert-PointFolder ([string]$a.folder) $pointId
            $startedUtc = (Get-Date).ToUniversalTime()
            $repo = $null; $checkpoint = $null; $staging = $null; $pointPath = $null; $created = $false
            $warnings = New-Object System.Collections.ArrayList
            try {
                $repo = Open-BackupTarget $a.target
                $pointPath = Join-Path $repo.root $folder
                if (Test-Path -LiteralPath $pointPath) { throw "The restore point folder already exists: $folder" }
                if ($repo.direct) {
                    $exportRoot = $pointPath
                } else {
                    $staging = Join-Path ([string]$a.target.stagingPath) ('hvportal-' + $pointId.ToString('N'))
                    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
                    $exportRoot = $staging
                }
                New-Item -ItemType Directory -Path $exportRoot -Force | Out-Null
                $created = $true
                [void]$applied.Add('folder')

                # A checkpoint left by a backup that was interrupted (agent or host stopped before its "finally") is
                # removed first; its name records the checkpoint type the VM had, which that run could not put back.
                foreach ($old in @(Get-VMSnapshot -VM $vm -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'HvPortal backup *' })) {
                    if ($old.Name -match '\[(Production|ProductionOnly|Standard)\]$' -and "$($vm.CheckpointType)" -ne $Matches[1]) {
                        $previous = $Matches[1]
                        Set-VM -VM $vm -CheckpointType $previous
                        [void]$warnings.Add("The checkpoint type of the VM was put back to $previous (left changed by an interrupted backup).")
                        $vm = Get-VmById $a.vmId
                    }
                    Remove-VMSnapshot -VMSnapshot $old -Confirm:$false
                    [void]$warnings.Add("A checkpoint left by an interrupted backup was removed: $($old.Name).")
                }
                $configured = "$($vm.CheckpointType)"
                if ($configured -eq 'Disabled') { throw 'Checkpoints are disabled for this VM (Settings > Checkpoint type): a consistent copy of the VM needs one.' }
                $requested = [string]$a.checkpointType
                $changed = $requested -ne $configured
                # The checkpoint type of the VM is changed only for the moment of the checkpoint, then put back.
                if ($changed) { Set-VM -VM $vm -CheckpointType $requested }
                try { $checkpoint = Checkpoint-VM -VM $vm -SnapshotName ('HvPortal backup ' + $pointId.ToString('D') + ' [' + $configured + ']') -Passthru }
                finally { if ($changed) { Set-VM -VM (Get-VmById $a.vmId) -CheckpointType $configured } }
                [void]$applied.Add('checkpoint')
                $used = "$($checkpoint.SnapshotType)"
                Export-VMSnapshot -VMSnapshot $checkpoint -Path $exportRoot
                [void]$applied.Add('exported')
                Remove-VMSnapshot -VMSnapshot $checkpoint -Confirm:$false
                $checkpoint = $null
                [void]$applied.Add('checkpointRemoved')

                # SHA-256 of every exported file, read on the host before any copy.
                $files = New-Object System.Collections.ArrayList
                $total = [int64]0
                $rootText = (Get-Item -LiteralPath $exportRoot).FullName.TrimEnd('\')
                foreach ($f in @(Get-ChildItem -LiteralPath $exportRoot -Recurse -File -Force)) {
                    $rel = $f.FullName.Substring($rootText.Length + 1)
                    [void]$files.Add(@{ path = $rel; sizeBytes = [int64]$f.Length; sha256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
                    $total += [int64]$f.Length
                }
                if ($files.Count -eq 0) { throw 'The export produced no file.' }
                if (-not $repo.direct) {
                    Copy-PointFiles $files $exportRoot $pointPath
                    [void]$applied.Add('copied')
                    if ([bool]$a.verifyAfterCopy) {
                        foreach ($f in $files) {
                            $h = (Get-FileHash -LiteralPath (Join-Path $pointPath $f.path) -Algorithm SHA256).Hash.ToLowerInvariant()
                            if ($h -ne $f.sha256) { throw "Verification failed: the copy of $($f.path) in the repository differs from the export." }
                        }
                        [void]$applied.Add('verified')
                    }
                }
                $cfg = Get-Config $vm
                $config = @($files | Where-Object { $_.path -like '*\Virtual Machines\*.vmcx' } | Select-Object -First 1)
                if ($config.Count -eq 0) { [void]$warnings.Add('No .vmcx configuration file in the export: the VM cannot be imported from this restore point, only its disks.') }
                $disks = @($files | Where-Object { $_.path -match '\.(vhdx|vhd)$' } | ForEach-Object { $_.path })
                $manifest = [ordered]@{
                    format = 1; pointId = $pointId.ToString('D'); vmId = "$($vm.Id)"; vmName = $vm.Name; hostName = $env:COMPUTERNAME
                    createdUtc = $startedUtc.ToString('o'); completedUtc = (Get-Date).ToUniversalTime().ToString('o')
                    checkpointTypeRequested = $requested; checkpointTypeUsed = $used
                    generation = [int]$cfg.generation; configurationVersion = [string]$cfg.version; processorCount = [int]$cfg.processorCount
                    memoryStartupMb = [int64]($cfg.memoryStartupBytes / 1MB)
                    networkAdapters = @($cfg.networkAdapters | ForEach-Object { @{ name = $_.name; switchName = $_.switchName; vlanId = [int]$_.vlanId } })
                    configFile = $(if ($config.Count -gt 0) { $config[0].path } else { $null }); disks = $disks; files = @($files); totalBytes = $total
                    warnings = @($warnings)
                }
                # Written last: a folder without manifest is an incomplete restore point.
                Set-Content -LiteralPath (Join-Path $pointPath 'hvportal-backup.json') -Value (ConvertTo-Json -InputObject $manifest -Depth 6) -Encoding UTF8
                [void]$applied.Add('manifest')
                Write-Response $true $manifest $null $applied
            } catch {
                # Nothing incomplete is left in the repository: only the folder created by this run is removed.
                if ($created -and $pointPath -and (Test-Path -LiteralPath $pointPath) -and -not (Test-Path -LiteralPath (Join-Path $pointPath 'hvportal-backup.json'))) {
                    Remove-Item -LiteralPath $pointPath -Recurse -Force -ErrorAction SilentlyContinue
                }
                throw
            } finally {
                if ($checkpoint) { Remove-VMSnapshot -VMSnapshot $checkpoint -Confirm:$false -ErrorAction SilentlyContinue }
                if ($staging -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
                Close-BackupTarget $repo
            }
        }

        'VerifyBackup' {
            $pointId = [guid][string]$a.pointId
            $folder = Assert-PointFolder ([string]$a.folder) $pointId
            $repo = $null
            try {
                $repo = Open-BackupTarget $a.target
                $pointPath = Join-Path $repo.root $folder
                $m = Read-Manifest $pointPath $pointId
                $problems = New-Object System.Collections.ArrayList
                $count = 0; $bytes = [int64]0
                foreach ($f in @($m.files)) {
                    $rel = [string]$f.path
                    $p = Join-Path $pointPath $rel
                    $item = Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
                    if (-not $item) { [void]$problems.Add("missing: $rel"); continue }
                    if ([int64]$item.Length -ne [int64]$f.sizeBytes) { [void]$problems.Add("size differs: $rel"); continue }
                    if ($f.sha256 -and (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() -ne [string]$f.sha256) { [void]$problems.Add("content differs (SHA-256): $rel"); continue }
                    $count++; $bytes += [int64]$item.Length
                }
                Write-Response $true @{ ok = ($problems.Count -eq 0); filesChecked = $count; bytesChecked = $bytes; problems = @($problems) } $null @('verified')
            } finally { Close-BackupTarget $repo }
        }

        'RemoveBackup' {
            $pointId = [guid][string]$a.pointId
            $folder = Assert-PointFolder ([string]$a.folder) $pointId
            $repo = $null
            try {
                $repo = Open-BackupTarget $a.target
                $pointPath = Join-Path $repo.root $folder
                if (-not (Test-Path -LiteralPath $pointPath)) {
                    Write-Response $true @{ removed = $false } $null @()
                } else {
                    # A manifest, when present, must be the one of this restore point (never delete another one).
                    if (Test-Path -LiteralPath (Join-Path $pointPath 'hvportal-backup.json')) { [void](Read-Manifest $pointPath $pointId) }
                    Remove-Item -LiteralPath $pointPath -Recurse -Force
                    $vmFolder = Join-Path $repo.root ($folder.Split('\')[0])
                    if ((Test-Path -LiteralPath $vmFolder) -and -not (Get-ChildItem -LiteralPath $vmFolder -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $vmFolder -Force }
                    Write-Response $true @{ removed = $true } $null @('removed')
                }
            } finally { Close-BackupTarget $repo }
        }

        'ListBackups' {
            $repo = $null
            try {
                $repo = Open-BackupTarget $a.target
                $list = New-Object System.Collections.ArrayList
                foreach ($vmDir in @(Get-ChildItem -LiteralPath $repo.root -Directory -Force -ErrorAction SilentlyContinue)) {
                    if ($vmDir.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$') { continue }
                    $vmPath = Join-Path $repo.root $vmDir.Name
                    foreach ($pointDir in @(Get-ChildItem -LiteralPath $vmPath -Directory -Force -ErrorAction SilentlyContinue)) {
                        $mp = Join-Path (Join-Path $vmPath $pointDir.Name) 'hvportal-backup.json'
                        if (-not (Test-Path -LiteralPath $mp)) { continue }
                        try { [void]$list.Add(@{ folder = "$($vmDir.Name)\$($pointDir.Name)"; manifest = (Get-Content -LiteralPath $mp -Raw | ConvertFrom-Json) }) } catch { }
                        if ($list.Count -ge 1000) { break }
                    }
                    if ($list.Count -ge 1000) { break }
                }
                Write-Response $true @($list) $null @()
            } finally { Close-BackupTarget $repo }
        }

        'RestoreBackup' {
            $pointId = [guid][string]$a.pointId
            $folder = Assert-PointFolder ([string]$a.folder) $pointId
            $mode = [string]$a.mode
            $repo = $null; $staging = $null; $newVm = $null; $vmFolder = $null; $createdFolder = $false
            $warnings = New-Object System.Collections.ArrayList
            try {
                $repo = Open-BackupTarget $a.source
                $pointPath = Join-Path $repo.root $folder
                $m = Read-Manifest $pointPath $pointId
                if (-not $m.configFile) { throw 'This restore point has no VM configuration: restore its disks instead.' }
                Assert-RelativePath ([string]$m.configFile)
                # Hyper-V reads the export itself: from a share opened with credentials it is first copied to the staging folder.
                $importRoot = $pointPath
                if (-not $repo.direct) {
                    $staging = Join-Path ([string]$a.source.stagingPath) ('hvportal-restore-' + [guid]::NewGuid().ToString('N'))
                    New-Item -ItemType Directory -Path $staging -Force | Out-Null
                    Copy-PointFiles $m.files $pointPath $staging
                    [void]$applied.Add('staged')
                    $importRoot = $staging
                }
                $vmcx = Join-Path $importRoot ([string]$m.configFile)

                if ($mode -eq 'Replace') {
                    $orig = Get-VmById ([string]$a.replaceVmId)
                    if ("$($orig.Id)" -ne [string]$m.vmId) { throw 'The VM to replace is not the VM this restore point was taken from.' }
                    if (Get-VMSnapshot -VM $orig -ErrorAction SilentlyContinue | Select-Object -First 1) { throw 'The VM has checkpoints: delete them, or restore as a new VM.' }
                    $configRoot = $orig.Path
                    $oldDisks = @(Get-VMHardDiskDrive -VM $orig | Where-Object { $_.Path } | ForEach-Object { $_.Path })
                    $vhdFolder = $(if ($oldDisks.Count -gt 0) { Split-Path -Path $oldDisks[0] -Parent } else { (Get-VMHost).VirtualHardDiskPath })
                    # The disks go back where the original ones are; only the fallback to the host default is checked.
                    if ($oldDisks.Count -eq 0) { Assert-StorageFolder $vhdFolder 'The default folder of the virtual disks of this host' }
                    # Checked BEFORE the original is touched: the restore point must be importable on this host (version,
                    # processor, memory...). A new id is used only for this check (the original still holds its own);
                    # Compare-VM copies nothing.
                    $precheck = Compare-VM -Path $vmcx -Copy -GenerateNewId -VirtualMachinePath $configRoot -SnapshotFilePath $configRoot -SmartPagingFilePath $configRoot -VhdDestinationPath $vhdFolder
                    foreach ($i in @($precheck.Incompatibilities)) {
                        if (-not ($i.Source -is [Microsoft.HyperV.PowerShell.VMNetworkAdapter])) { throw "The restore point cannot be imported on this host: $($i.Message) The original VM was not changed." }
                    }
                    if ("$($orig.State)" -ne 'Off') { Stop-VM -VM $orig -TurnOff -Force -Confirm:$false; [void]$applied.Add('turnedOff') }
                    Remove-VM -VM $orig -Force
                    [void]$applied.Add('originalRemoved')
                    # The disks of the original are kept, renamed: nothing is destroyed by a restore.
                    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
                    $renamed = New-Object System.Collections.ArrayList
                    foreach ($d in $oldDisks) {
                        if (Test-Path -LiteralPath $d) {
                            Rename-Item -LiteralPath $d -NewName ((Split-Path -Path $d -Leaf) + ".pre-restore-$stamp")
                            [void]$renamed.Add($d)
                            [void]$applied.Add("kept:$d.pre-restore-$stamp")
                        }
                    }
                    try {
                        $report = Compare-VM -Path $vmcx -Copy -VirtualMachinePath $configRoot -SnapshotFilePath $configRoot -SmartPagingFilePath $configRoot -VhdDestinationPath $vhdFolder
                        foreach ($i in @($report.Incompatibilities)) {
                            if ($i.Source -is [Microsoft.HyperV.PowerShell.VMNetworkAdapter]) { $i.Source | Disconnect-VMNetworkAdapter; [void]$warnings.Add("Network adapter disconnected: $($i.Message)") }
                            else { throw "The restore point cannot be imported on this host: $($i.Message)" }
                        }
                        $newVm = Import-VM -CompatibilityReport $report
                    } catch {
                        foreach ($d in $renamed) {
                            $old = "$d.pre-restore-$stamp"
                            if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $d)) { Rename-Item -LiteralPath $old -NewName (Split-Path -Path $d -Leaf) }
                        }
                        throw "The import failed: $($_.Exception.Message) The original VM was removed from Hyper-V; its disks were put back under their names in $vhdFolder."
                    }
                } else {
                    $name = [string]$a.newName
                    if (Get-VM -Name $name -ErrorAction SilentlyContinue) { throw "A VM named '$name' already exists on this host." }
                    $base = $(if ($a.location) { [string]$a.location } else { (Get-VMHost).VirtualMachinePath })
                    if (-not $a.location) { Assert-StorageFolder $base 'The default folder of the VMs of this host' }
                    $vmFolder = Join-Path $base (Get-VmFolderName $name)
                    if ((Test-Path -LiteralPath $vmFolder) -and (Get-ChildItem -LiteralPath $vmFolder -Force | Select-Object -First 1)) { throw "The folder $vmFolder already exists and is not empty." }
                    $vhdDest = Join-Path $vmFolder 'Virtual Hard Disks'
                    New-Item -ItemType Directory -Path $vhdDest -Force | Out-Null
                    $createdFolder = $true
                    $report = Compare-VM -Path $vmcx -Copy -GenerateNewId -VirtualMachinePath $vmFolder -SnapshotFilePath $vmFolder -SmartPagingFilePath $vmFolder -VhdDestinationPath $vhdDest
                    foreach ($i in @($report.Incompatibilities)) {
                        if ($i.Source -is [Microsoft.HyperV.PowerShell.VMNetworkAdapter]) { $i.Source | Disconnect-VMNetworkAdapter; [void]$warnings.Add("Network adapter disconnected: $($i.Message)") }
                        else { throw "The restore point cannot be imported on this host: $($i.Message)" }
                    }
                    $newVm = Import-VM -CompatibilityReport $report
                    Rename-VM -VM $newVm -NewName $name
                    $newVm = Get-VM -Id $newVm.Id
                }
                [void]$applied.Add('imported')

                $network = [string]$a.network
                if ($network -eq 'Disconnected') {
                    Get-VMNetworkAdapter -VM $newVm | Disconnect-VMNetworkAdapter
                    [void]$applied.Add('networkDisconnected')
                } elseif ($network -eq 'Switch') {
                    $sw = Get-VMSwitch -Name ([string]$a.switchName) -ErrorAction SilentlyContinue | Where-Object { $_.Name -ceq [string]$a.switchName } | Select-Object -First 1
                    if (-not $sw) { throw "Virtual switch not found: $($a.switchName)" }
                    Get-VMNetworkAdapter -VM $newVm | Connect-VMNetworkAdapter -VMSwitch $sw
                    [void]$applied.Add("network:$($sw.Name)")
                }

                $testPassed = $null; $testSeconds = $null; $testDetail = $null
                if ($mode -eq 'Test') {
                    # Isolated boot check: started with its adapters disconnected, healthy when the guest heartbeat reports OK.
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    try {
                        Start-VM -VM $newVm
                        $limit = [int]$a.testTimeoutSeconds
                        $heartbeat = ''
                        while ($clock.Elapsed.TotalSeconds -lt $limit) {
                            Start-Sleep -Seconds 5
                            $now = Get-VM -Id $newVm.Id
                            $heartbeat = "$($now.Heartbeat)"
                            if ($heartbeat -like 'Ok*') { break }
                            if ("$($now.State)" -ne 'Running') { break }
                        }
                        $testSeconds = [int]$clock.Elapsed.TotalSeconds
                        $testPassed = $heartbeat -like 'Ok*'
                        $testDetail = $(if ($testPassed) { "The VM started and the guest reported heartbeat $heartbeat after $testSeconds s." }
                            else { "No healthy heartbeat within $limit s (last: $(if ($heartbeat) { $heartbeat } else { 'none' }), state $((Get-VM -Id $newVm.Id).State)). The integration services may be off in the guest, or the guest did not boot." })
                    } finally {
                        # The test VM never stays: turned off, removed, and its files deleted.
                        Stop-VM -VM (Get-VM -Id $newVm.Id) -TurnOff -Force -Confirm:$false -ErrorAction SilentlyContinue
                        Remove-VM -VM (Get-VM -Id $newVm.Id) -Force -ErrorAction SilentlyContinue
                        if ($vmFolder -and $createdFolder) { Remove-Item -LiteralPath $vmFolder -Recurse -Force -ErrorAction SilentlyContinue }
                        [void]$applied.Add('testVmRemoved')
                    }
                } elseif ([bool]$a.startAfter) {
                    Start-VM -VM $newVm
                    [void]$applied.Add('started')
                }
                Write-Response $true @{
                    mode = $mode; vmId = $(if ($mode -eq 'Test') { $null } else { "$($newVm.Id)" }); name = $newVm.Name; path = $(if ($mode -eq 'Test') { $null } else { $newVm.Path })
                    testPassed = $testPassed; testSeconds = $testSeconds; testDetail = $testDetail; warnings = @($warnings)
                } $null $applied
            } catch {
                # A new VM that failed half way is not left behind (Replace keeps the disks, see above).
                if ($mode -ne 'Replace' -and $newVm -and $mode -ne 'Test') {
                    Remove-VM -VM (Get-VM -Id $newVm.Id -ErrorAction SilentlyContinue) -Force -ErrorAction SilentlyContinue
                }
                if ($mode -ne 'Replace' -and $createdFolder -and $vmFolder) { Remove-Item -LiteralPath $vmFolder -Recurse -Force -ErrorAction SilentlyContinue }
                throw
            } finally {
                if ($staging -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
                Close-BackupTarget $repo
            }
        }

        'RestoreBackupDisk' {
            $pointId = [guid][string]$a.pointId
            $folder = Assert-PointFolder ([string]$a.folder) $pointId
            $repo = $null
            try {
                $repo = Open-BackupTarget $a.source
                $pointPath = Join-Path $repo.root $folder
                $m = Read-Manifest $pointPath $pointId
                $disk = [string]$a.disk
                Assert-RelativePath $disk
                if (@($m.disks) -notcontains $disk) { throw 'The disk is not part of this restore point.' }
                $destFolder = [string]$a.destinationFolder
                if (-not (Test-Path -LiteralPath $destFolder)) { New-Item -ItemType Directory -Path $destFolder -Force | Out-Null }
                $leaf = Split-Path -Path $disk -Leaf
                $dest = Join-Path $destFolder ('{0}-restored-{1}{2}' -f [IO.Path]::GetFileNameWithoutExtension($leaf), (Get-Date -Format 'yyyyMMddHHmmss'), [IO.Path]::GetExtension($leaf))
                if (Test-Path -LiteralPath $dest) { throw "File already exists: $dest" }
                Copy-Item -LiteralPath (Join-Path $pointPath $disk) -Destination $dest
                [void]$applied.Add("copied:$dest")
                $attached = $false; $target = $null
                if ($a.attachVmId) {
                    $target = Get-VmById $a.attachVmId
                    try { Add-VMHardDiskDrive -VM $target -ControllerType SCSI -Path $dest; $attached = $true; [void]$applied.Add("attached:$($target.Name)") }
                    catch { throw "The disk was restored to $dest but could not be attached to $($target.Name): $($_.Exception.Message)" }
                }
                Write-Response $true @{ path = $dest; attached = $attached; vmId = $(if ($target) { "$($target.Id)" } else { $null }) } $null $applied
            } finally { Close-BackupTarget $repo }
        }

        'MountBackupDisk' {
            # Read-only attach of one disk of a restore point, to browse its files. Only drive-letter volumes are reported.
            $pointId = [guid][string]$a.pointId
            $folder = Assert-PointFolder ([string]$a.folder) $pointId
            $repo = $null; $local = $null; $staged = $false; $mounted = $false
            try {
                $repo = Open-BackupTarget $a.source
                $pointPath = Join-Path $repo.root $folder
                $m = Read-Manifest $pointPath $pointId
                $disk = [string]$a.disk
                Assert-RelativePath $disk
                if (@($m.disks) -notcontains $disk) { throw 'The disk is not part of this restore point.' }
                if ($repo.direct) {
                    $local = (Get-Item -LiteralPath (Join-Path $pointPath $disk)).FullName
                } else {
                    $local = Join-Path ([string]$a.source.stagingPath) ('hvportal-mount-' + [guid]::NewGuid().ToString('N') + [IO.Path]::GetExtension($disk))
                    Copy-Item -LiteralPath (Join-Path $pointPath $disk) -Destination $local
                    $staged = $true
                }
                $vhd = Mount-VHD -Path $local -ReadOnly -Passthru
                $mounted = $true
                $number = $vhd.DiskNumber
                if ($null -eq $number) { $number = (Get-VHD -Path $local).DiskNumber }
                $volumes = New-Object System.Collections.ArrayList
                for ($try = 0; $try -lt 10 -and $volumes.Count -eq 0; $try++) {
                    Start-Sleep -Seconds 1
                    $drive = Get-CimInstance Win32_DiskDrive -Filter "Index=$([int]$number)"
                    if (-not $drive) { continue }
                    foreach ($part in @(Get-CimAssociatedInstance -InputObject $drive -ResultClassName Win32_DiskPartition)) {
                        foreach ($ld in @(Get-CimAssociatedInstance -InputObject $part -ResultClassName Win32_LogicalDisk)) {
                            [void]$volumes.Add(@{ root = "$($ld.DeviceID)\"; label = $ld.VolumeName; fileSystem = $ld.FileSystem; sizeBytes = [int64]$ld.Size })
                        }
                    }
                }
                if ($volumes.Count -eq 0) {
                    throw 'The disk was attached but Windows assigned no drive letter to its volumes (BitLocker, a file system Windows cannot read, or automount disabled). Restore the disk to a VM instead.'
                }
                Write-Response $true @{ diskPath = $local; staged = $staged; volumes = @($volumes) } $null @('mounted')
            } catch {
                if ($mounted) { Dismount-VHD -Path $local -ErrorAction SilentlyContinue }
                if ($staged -and $local) { Remove-Item -LiteralPath $local -Force -ErrorAction SilentlyContinue }
                throw
            } finally { Close-BackupTarget $repo }
        }

        'DismountBackupDisk' {
            # Called by the agent with a disk from its own list of attached backup disks.
            $path = [string]$a.diskPath
            $ext = [IO.Path]::GetExtension($path)
            if ($ext -ine '.vhdx' -and $ext -ine '.vhd') { throw 'Not a virtual disk file.' }
            $v = Get-VHD -Path $path -ErrorAction SilentlyContinue
            if ($v -and $v.Attached) { Dismount-VHD -Path $path }
            if ([bool]$a.deleteFile -and (Split-Path -Path $path -Leaf) -like 'hvportal-mount-*') { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
            Write-Response $true @{ detached = $true } $null @('dismounted')
        }

        default { throw "Unsupported operation." }
    }
} catch {
    Write-Response $false $null $_.Exception.Message $applied
}
