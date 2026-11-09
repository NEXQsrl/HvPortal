using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>Protocol 8: VM templates (NewTemplate, RemoveTemplate, NewVm from a template): validation, protocol, script rules, timeouts.</summary>
public sealed class TemplateOperationTests
{
    private const string Library = @"T:\Templates";

    private static string Script()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Ops.ps1"));
        return string.Join('\n', System.Text.RegularExpressions.Regex.Replace(source, "(?s)<#.*?#>", "").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    private static string Branch(string script, string op)
    {
        var start = script.IndexOf($"'{op}' {{", StringComparison.Ordinal);
        Assert.True(start >= 0, op);
        var next = System.Text.RegularExpressions.Regex.Match(script[(start + 10)..], @"\n        '[A-Za-z]+' \{");
        return next.Success ? script.Substring(start, next.Index + 10) : script[start..];
    }

    [Fact]
    public void A_template_folder_is_one_level_below_the_library_and_named_by_an_id()
    {
        var vm = Guid.NewGuid();
        var folder = $@"{Library}\{Guid.NewGuid():D}";
        // Text rules (shared with the gateway); the agent then checks the library folder on the real file system.
        static string? New(Guid vmId, string f, params string[] roots) => OperationValidation.Validate(new NewTemplateRequest { VmId = vmId, Folder = f, Roots = roots });
        static string? Remove(string f, params string[] roots) => OperationValidation.Validate(new TemplateFolderRequest { Folder = f, Roots = roots });
        static string? OnAgent(HvOperation op, object args) => PowerShellOperationRunner.Validate(op, HvOperations.ToElement(args));

        Assert.Null(New(vm, folder, Library));
        // The library folder may not exist yet: the first template creates it.
        Assert.Null(New(vm, $@"T:\NewLibrary\{Guid.NewGuid():D}", @"T:\NewLibrary"));
        Assert.Equal("invalid_vm", New(Guid.Empty, folder, Library));
        Assert.Equal("invalid_template_folder", New(vm, folder));
        Assert.Equal("invalid_template_folder", New(vm, $@"{Library}\not-an-id", Library));
        Assert.Equal("invalid_template_folder", New(vm, $@"{Library}\sub\{Guid.NewGuid():D}", Library));
        Assert.Equal("invalid_template_folder", New(vm, $@"T:\Other\{Guid.NewGuid():D}", Library));
        Assert.Equal("invalid_template_folder", New(vm, $@"{Library}\..\{Guid.NewGuid():D}", Library));
        Assert.Equal("invalid_template_folder", New(vm, Library, Library));
        Assert.Equal("invalid_template_folder", New(vm, $@"C:\Windows\{Guid.NewGuid():D}", @"C:\Windows"));
        Assert.Equal("invalid_template_folder", New(vm, $@"\\nas\templates\{Guid.NewGuid():D}", @"\\nas\templates"));
        // The agent applies the same rules before PowerShell runs, and wants the library on a local fixed drive of the
        // host, outside hidden or system folders and junctions (same check as a VM location).
        Assert.Equal("invalid_template_folder", OnAgent(HvOperation.NewTemplate, new NewTemplateRequest { VmId = vm, Folder = $@"{Library}\not-an-id", Roots = [Library] }));
        Assert.Equal("invalid_vm", OnAgent(HvOperation.NewTemplate, new NewTemplateRequest { VmId = Guid.Empty, Folder = folder, Roots = [Library] }));
        var noSuchDrive = OnAgent(HvOperation.NewTemplate, new NewTemplateRequest { VmId = vm, Folder = $@"0:\Templates\{Guid.NewGuid():D}", Roots = [@"0:\Templates"] });
        Assert.NotNull(noSuchDrive);
        var hidden = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HvPortalTemplates");
        Assert.NotNull(OnAgent(HvOperation.NewTemplate, new NewTemplateRequest { VmId = vm, Folder = $@"{hidden}\{Guid.NewGuid():D}", Roots = [hidden] }));
        Assert.Equal("invalid_template_folder", OnAgent(HvOperation.RemoveTemplate, new TemplateFolderRequest { Folder = Library, Roots = [Library] }));

        Assert.Null(Remove(folder, Library));
        Assert.Equal("invalid_template_folder", Remove(Library, Library));
        Assert.Equal("invalid_template_folder", Remove($@"{Library}\{Guid.NewGuid():D}\disk0.vhdx", Library));
        Assert.Equal("invalid_template_folder", Remove(@"T:\", @"T:\"));
        Assert.Equal("invalid_template_folder", Remove($@"T:\VMs\{Guid.NewGuid():D}", Library));
    }

    [Fact]
    public void A_vm_from_a_template_only_takes_disk_files_inside_the_template_library()
    {
        var folder = $@"{Library}\{Guid.NewGuid():D}";
        static string? Check(NewVmRequest r) => OperationValidation.Validate(r);
        var ok = new NewVmRequest { Name = "web01", TemplateDisks = [$@"{folder}\disk0.vhdx", $@"{folder}\disk1.vhd"], TemplateRoots = [Library], TemplateExtraDiskGb = 40 };
        Assert.Null(Check(ok));
        Assert.Equal("invalid_template", Check(ok with { TemplateDisks = [@"T:\VMs\other\disk.vhdx"] }));
        Assert.Equal("invalid_template", Check(ok with { TemplateDisks = [$@"{Library}\..\VMs\disk.vhdx"] }));
        Assert.Equal("invalid_template", Check(ok with { TemplateDisks = [$@"{folder}\setup.iso"] }));
        Assert.Equal("invalid_template", Check(ok with { TemplateDisks = [$@"{folder}\disk0.vhdx; Remove-Item C:\"] }));
        Assert.Equal("invalid_template", Check(ok with { TemplateRoots = [] }));
        Assert.Equal("invalid_template", Check(ok with { TemplateRoots = [@"C:\Windows"], TemplateDisks = [@"C:\Windows\x\disk0.vhdx"] }));
        Assert.Equal("invalid_template", Check(ok with { TemplateDisks = Enumerable.Range(0, 17).Select(i => $@"{folder}\disk{i}.vhdx").ToList() }));
        // Template fields without template disks are refused, not ignored.
        Assert.Equal("invalid_template", Check(new NewVmRequest { Name = "web01", TemplateRoots = [Library] }));
        Assert.Equal("invalid_template", Check(new NewVmRequest { Name = "web01", TemplateExtraDiskGb = 10 }));
        // On the agent the same rules apply first; then the library must exist on a fixed drive of the host.
        Assert.Equal("invalid_template", PowerShellOperationRunner.Validate(HvOperation.NewVm, HvOperations.ToElement(ok with { TemplateDisks = [@"T:\VMs\other\disk.vhdx"] })));
        Assert.NotNull(PowerShellOperationRunner.Validate(HvOperation.NewVm,
            HvOperations.ToElement(ok with { TemplateRoots = [@"0:\Missing"], TemplateDisks = [$@"0:\Missing\{Guid.NewGuid():D}\disk0.vhdx"] })));
    }

    /// <summary>
    /// The path rules run on text sent by clients: they must answer at once whatever the text. The first version of the
    /// folder pattern took exponential time on a long name followed by a refused character.
    /// </summary>
    [Fact]
    public void Path_rules_answer_at_once_on_long_refused_names()
    {
        string[] hostile =
        [
            @"D:\" + new string('a', 150) + ":",
            $@"T:\Templates\{Guid.NewGuid():D}\disk0.vhdx; Remove-Item C:\",
            @"D:\" + string.Join(@"\", Enumerable.Repeat(new string('b', 30), 5)) + "|",
            @"D:\" + new string('c', 190) + "*",
        ];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var path in hostile)
        {
            Assert.False(HostPaths.IsValidLocalFolder(path));
            Assert.False(HostPaths.IsValidFilePath(path + @"\disk0.vhdx"));
            Assert.NotNull(HostPaths.ValidateLocation(path));
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");

        // What is accepted and refused did not change.
        foreach (var ok in new[] { @"D:\", @"D:\VMs", @"D:\VMs\", @"d:\VMs\Prod 1\a.b", @"E:\a\b\c\d\e" })
        {
            Assert.True(HostPaths.IsValidLocalFolder(ok), ok);
        }
        foreach (var bad in new[] { "D:", @"D:\\VMs", @"D:\VMs\\x", @"\\nas\share", "D:/VMs", @"D:\VMs\..\x", @"D:\a*b", @"D:\a:b", @"D:\VMs\ ", "", @"\VMs" })
        {
            Assert.False(HostPaths.IsValidLocalFolder(bad), bad);
        }
    }

    [Fact]
    public void Protocol_8_is_required_for_templates_and_long_copies_get_a_longer_timeout()
    {
        AgentCommand Op(HvOperation op, object? args = null, int timeout = 4 * 3600) => new()
        {
            CommandId = Guid.NewGuid(), Kind = AgentCommandKind.HyperVOperation, Operation = op, TimeoutSeconds = timeout,
            OperationArgs = args is null ? null : HvOperations.ToElement(args),
        };
        var folder = $@"{Library}\{Guid.NewGuid():D}";
        var fromTemplate = new NewVmRequest { Name = "web01", TemplateDisks = [$@"{folder}\disk0.vhdx"], TemplateRoots = [Library] };
        Assert.Equal(11, AgentProtocol.Version);
        Assert.Equal(8, AgentProtocol.RequiredVersion(Op(HvOperation.NewTemplate)));
        Assert.Equal(8, AgentProtocol.RequiredVersion(Op(HvOperation.RemoveTemplate)));
        // An older agent would ignore the template disks and create an empty VM.
        Assert.Equal(8, AgentProtocol.RequiredVersion(Op(HvOperation.NewVm, fromTemplate)));
        Assert.True(AgentProtocol.RequiredVersion(Op(HvOperation.NewVm, new NewVmRequest { Name = "plain" })) < 8);

        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.NewTemplate)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.RemoveTemplate)));
        // Copying disks may take hours; everything else keeps the 30 minute ceiling.
        Assert.Equal(TimeSpan.FromHours(4), CommandExecutor.TimeoutFor(Op(HvOperation.NewTemplate), HvOperation.NewTemplate));
        Assert.Equal(TimeSpan.FromHours(4), CommandExecutor.TimeoutFor(Op(HvOperation.NewVm, fromTemplate), HvOperation.NewVm));
        Assert.Equal(TimeSpan.FromHours(12), CommandExecutor.TimeoutFor(Op(HvOperation.NewTemplate, timeout: 100 * 3600), HvOperation.NewTemplate));
        Assert.Equal(TimeSpan.FromMinutes(30), CommandExecutor.TimeoutFor(Op(HvOperation.NewVm, new NewVmRequest { Name = "plain" }), HvOperation.NewVm));
        Assert.Equal(TimeSpan.FromMinutes(30), CommandExecutor.TimeoutFor(Op(HvOperation.RemoveTemplate), HvOperation.RemoveTemplate));
    }

    [Fact]
    public void Script_copies_disks_and_never_changes_the_source_vm_or_the_template()
    {
        var script = Script();
        var capture = Branch(script, "NewTemplate");
        // Only from a VM that is off, without checkpoints, with plain virtual disks.
        Assert.Contains("-ne 'Off'", capture);
        Assert.Contains("Get-VMSnapshot", capture);
        Assert.Contains("ParentPath", capture);
        Assert.Contains("pass-through", capture);
        Assert.Contains("Get-TemplateFolder ([string]$a.folder) $a.roots -CreateLibrary", capture);
        Assert.Contains("Copy-Item -LiteralPath", capture);
        // The VM is read, never modified or removed.
        foreach (var forbidden in new[] { "Remove-VM ", "Set-VM", "Stop-VM", "Remove-VMHardDiskDrive", "Move-Item", "Export-VM" })
        {
            Assert.DoesNotContain(forbidden, capture);
        }
        // Nothing is left behind when the copy fails.
        Assert.Contains("Remove-Item -LiteralPath $folder -Recurse -Force", capture);

        var remove = Branch(script, "RemoveTemplate");
        Assert.Contains("Get-TemplateFolder ([string]$a.folder) $a.roots", remove);
        Assert.DoesNotContain("-CreateLibrary", remove);
        Assert.Contains("nothing was deleted", remove);
        // Files one by one, then the empty folder: never a recursive delete of a path that came in a request.
        Assert.DoesNotContain("-Recurse", remove);

        var create = Branch(script, "NewVm");
        Assert.Contains("Get-TemplateDisk ([string]$_) $a.templateRoots", create);
        Assert.Contains("Copy-Item -LiteralPath $templateDisks[$i].FullName -Destination $dest", create);
        Assert.DoesNotContain("Move-Item", create);
        Assert.Contains("Resize-VHD -Path $diskPath", create);
        Assert.Contains("foreach ($c in $copied) { Remove-Item -LiteralPath $c -Force", create);

        // The helpers refuse paths outside the library and paths through junctions or symbolic links.
        Assert.Contains("The template folder is not inside the template library of this host.", script);
        Assert.Contains("Assert-NoReparsePoint", script);
        Assert.Contains("[guid]::TryParse([IO.Path]::GetFileName($full), [ref]$id)", script);
    }
}
