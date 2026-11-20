using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace NetWatch.Service.Bootstrap;

/// <summary>Writes files readable only by SYSTEM and local Administrators (e.g. the initial admin password).</summary>
public static class SecureFile
{
    public static void WriteRestricted(string path, string content)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));

        // When running interactively (development), also allow the current user so they can read it.
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var info = new FileInfo(path);
        using var stream = info.Create(FileMode.CreateNew, FileSystemRights.WriteData | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.None, security);
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }
}
