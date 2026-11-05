#Requires -Version 5.1
<#
  HvPortal agent operations (Windows PowerShell 5.1, Hyper-V module).
  Invoked ONLY by HvPortal.Agent: one JSON request on stdin -> one JSON response on stdout.
  Security rules:
   - closed set of operations (switch below); anything else is refused;
   - every value is cast to an explicit type and passed as a cmdlet parameter (splatting of fixed keys);
   - no Invoke-Expression, no script blocks built from input, no Start-Process;
   - VMs are always resolved by Id (names are not unique in Hyper-V);
   - new files are created only under the host default VHD folder (Get-VMHost), never at caller-chosen paths;
   - ISO paths must be inside the allowed library roots provided by the agent.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
# UTF-8 both ways (Windows PowerShell 5.1 defaults to the OEM code page).
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false

function Write-Response([bool]$ok, $result, [string]$errorText, $applied) {
    $obj = @{ ok = $ok; result = $result; error = $errorText; applied = @($applied) }
    [Console]::Out.Write((ConvertTo-Json -InputObject $obj -Depth 8 -Compress))
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

$applied = New-Object System.Collections.ArrayList
try {
    $raw = [Console]::In.ReadToEnd()
    if ($raw.Length -gt 65536) { throw 'Request too large.' }
    $req = ConvertFrom-Json -InputObject $raw
    $a = $req.args
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
                    Add-VMNetworkAdapter -VM $vm -SwitchName $sw.Name
                } else {
                    Add-VMNetworkAdapter -VM $vm
                }
                [void]$applied.Add('addNetworkAdapter')
            }

            if ($a.newDisk) {
                $root = (Get-VMHost).VirtualHardDiskPath
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
            $h = Get-VMHost
            $gen = [int16]$a.generation
            $params = @{ Name = $name; Generation = $gen; MemoryStartupBytes = [int64]$a.memoryStartupMb * 1MB }
            if ($a.switchName) {
                $sw = Get-VMSwitch -Name ([string]$a.switchName) -ErrorAction SilentlyContinue | Where-Object { $_.Name -ceq [string]$a.switchName } | Select-Object -First 1
                if (-not $sw) { throw "Virtual switch not found: $($a.switchName)" }
                $params.SwitchName = $sw.Name
            }
            $diskPath = $null
            if ([int64]$a.diskSizeGb -gt 0) {
                $diskPath = Join-Path $h.VirtualHardDiskPath ((Get-SafeName $name) + '.vhdx')
                if (Test-Path -LiteralPath $diskPath) { throw "File already exists: $diskPath" }
                $size = [uint64]$a.diskSizeGb * 1GB
                if ([bool]$a.diskFixed) { New-VHD -Path $diskPath -SizeBytes $size -Fixed | Out-Null; $params.VHDPath = $diskPath }
                else { $params.NewVHDPath = $diskPath; $params.NewVHDSizeBytes = $size }
            } else {
                $params.NoVHD = $true
            }
            $vm = New-VM @params
            [void]$applied.Add('created')
            $warning = $null
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
                if ([bool]$a.startAfterCreate) { Start-VM -VM $vm; [void]$applied.Add('started') }
            } catch {
                $warning = "The VM was created but its configuration is incomplete: $($_.Exception.Message)"
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
            Restore-VMSnapshot -VMSnapshot $s -Confirm:$false
            Write-Response $true @{ id = "$($s.Id)" } $null @('restored')
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

        default { throw "Unsupported operation." }
    }
} catch {
    Write-Response $false $null $_.Exception.Message $applied
}
