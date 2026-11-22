using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using NetWatch.Core.Configuration;

namespace NetWatch.Tests;

/// <summary>
/// BUG-009: the installer's data-folder ACL step left every existing file with an empty ACL (reinstall/upgrade), so
/// the service could not read its settings and crashed at start-up. These tests run the exact icacls arguments from
/// installer/NetWatch.Installer/netwatch.iss against a real folder that already holds files.
/// </summary>
public sealed class QaInstallerAclTests : IDisposable
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly string Me = WindowsIdentity.GetCurrent().User!.Value;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-qa-acl-" + Guid.NewGuid().ToString("N"));

    public QaInstallerAclTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "certs"));
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "data", "netwatch.db"), "db");
        File.WriteAllText(Path.Combine(_root, "certs", "console.pfx"), "pfx");
    }

    public void Dispose()
    {
        Icacls($"\"{_root}\" /reset /T /C /Q");
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Installer_acl_step_keeps_existing_files_readable_by_system_and_administrators()
    {
        RunInstallerAclSteps();
        AssertSecured();
    }

    [Fact]
    public void Installer_acl_step_repairs_files_emptied_by_the_2_0_0_rc1_installer()
    {
        // The rc1 command (one icacls call with /T). The current user is added only so the test can inspect the result.
        Icacls($"\"{_root}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *{Me}:(OI)(CI)F /T /C /Q");
        Assert.Empty(Rules(Path.Combine(_root, "appsettings.json"))); // the defect: no one, not even SYSTEM, may read it

        RunInstallerAclSteps();
        AssertSecured();
    }

    [Fact]
    public async Task Service_reports_an_unreadable_data_folder_instead_of_crashing()
    {
        var settings = Path.Combine(_root, "appsettings.json");
        var file = new FileInfo(settings);
        var security = file.GetAccessControl();
        var deny = new FileSystemAccessRule(new SecurityIdentifier(Me), FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        file.SetAccessControl(security);
        try
        {
            var exit = await NetWatch.Service.Program.Main(["--self-test", "--data-dir", _root, "--SelfTest:Seconds=1"]);
            Assert.Equal(6, exit);

            var message = NetWatch.Service.Program.DescribeDataFolderError(new NetWatchPaths(_root), new UnauthorizedAccessException($"Access to the path '{settings}' is denied."));
            Assert.Contains(settings, message);
            Assert.Contains("/reset /T", message);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            file.SetAccessControl(security);
        }
    }

    /// <summary>The three icacls calls of SecureDataFolder, with the arguments read from netwatch.iss.</summary>
    private void RunInstallerAclSteps()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "NetWatch.Installer", "netwatch.iss"));
        string Define(string name) => Regex.Match(iss, $"#define {name} \"([^\"]+)\"").Groups[1].Value is { Length: > 0 } v ? v : throw new InvalidOperationException($"{name} not found in netwatch.iss");

        Assert.Contains($"'\"' + Dir + '\" {{#AclRootArgs}}'", iss);
        Assert.Contains($"'\"' + Dir + '\\*\" {{#AclChildArgs}}'", iss);

        // Taking ownership needs elevation; without it icacls reports failure and the next steps still work.
        Icacls($"\"{_root}\" {Define("AclOwnerArgs")}");
        // As in the installer, plus the current user so the (usually non-elevated) test can still read the folder.
        var rootArgs = Define("AclRootArgs").Replace("/C /Q", $"*{Me}:(OI)(CI)F /C /Q");
        Assert.Equal(0, Icacls($"\"{_root}\" {rootArgs}"));
        Assert.Equal(0, Icacls($"\"{_root}\\*\" {Define("AclChildArgs")}"));
    }

    private void AssertSecured()
    {
        var rootSecurity = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(rootSecurity.AreAccessRulesProtected, "the data folder must not inherit from C:\\ProgramData");

        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Append(_root))
        {
            var rules = Rules(path);
            Assert.True(rules.Any(r => r.IdentityReference.Equals(System) && r.AccessControlType == AccessControlType.Allow && r.FileSystemRights.HasFlag(FileSystemRights.FullControl)), $"SYSTEM has no full control of {path}");
            Assert.True(rules.Any(r => r.IdentityReference.Equals(Admins) && r.AccessControlType == AccessControlType.Allow && r.FileSystemRights.HasFlag(FileSystemRights.FullControl)), $"Administrators have no full control of {path}");
            Assert.DoesNotContain(rules, r => r.IdentityReference.Value is "S-1-1-0" or "S-1-5-32-545" or "S-1-5-11"); // Everyone, Users, Authenticated Users
        }

        Assert.Equal("db", File.ReadAllText(Path.Combine(_root, "data", "netwatch.db")));
    }

    private static List<FileSystemAccessRule> Rules(string path)
    {
        FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
    }

    private static int Icacls(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "icacls.exe"), args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetWatch.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found");
    }
}
