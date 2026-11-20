using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Security;
using System.Xml.Linq;
using NetWatch.Core;

namespace NetWatch.Collectors.Servers.Windows;

/// <summary>A WMI row: property name → value as invariant text (null when absent/NULL).</summary>
public sealed class WmiRow : Dictionary<string, string?>
{
    public WmiRow()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

/// <summary>A connection to a server's WMI provider, over WinRM (WS-Management) or DCOM.</summary>
public interface IWmiSession : IDisposable
{
    ServerCollectionMethod Method { get; }

    /// <summary>Runs a WQL query in a namespace such as "root/cimv2".</summary>
    List<WmiRow> Query(string wmiNamespace, string wql);

    /// <summary>Invokes a static method of a WMI class (e.g. StdRegProv.EnumKey) and returns its output parameters.</summary>
    WmiRow Invoke(string wmiNamespace, string className, string method, IReadOnlyList<(string Name, object Value)> parameters);
}

/// <summary>Credentials split into domain / user for WMI and event-log sessions.</summary>
public readonly record struct WindowsAccount(string? Domain, string User, string Password)
{
    public static WindowsAccount From(Core.Credentials.CredentialSecret s)
    {
        var user = s.Username ?? string.Empty;
        var domain = s.Domain;
        var slash = user.IndexOf('\\');
        if (slash > 0)
        {
            domain = user[..slash];
            user = user[(slash + 1)..];
        }

        return new WindowsAccount(string.IsNullOrWhiteSpace(domain) ? null : domain.Trim(), user.Trim(), s.Password ?? string.Empty);
    }

    public string QualifiedUser => Domain is null || User.Contains('@') ? User : $"{Domain}\\{User}";

    public SecureString SecurePassword()
    {
        var secure = new SecureString();
        foreach (var c in Password)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }
}

/// <summary>Detects whether a host name/address refers to this machine (WMI/event logs refuse credentials for local connections).</summary>
public static class LocalHost
{
    private static readonly Lazy<HashSet<string>> Names = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", ".", Environment.MachineName };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    set.Add(a.Address.ToString());
                }
            }

            set.Add(Dns.GetHostName());
        }
        catch (NetworkInformationException)
        {
        }

        return set;
    });

    public static bool Is(string host) => Names.Value.Contains(host.Trim());
}

/// <summary>Parsing of WMI text values (numbers, booleans, CIM datetimes in DMTF or ISO 8601 form).</summary>
public static class WmiValue
{
    public static long? Long(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
        : ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) && u <= long.MaxValue ? (long)u
        : null;

    public static double? Double(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    public static bool? Bool(string? s) => bool.TryParse(s, out var v) ? v : null;

    /// <summary>Parses "20261119083000.500000+300" (DMTF, DCOM) or "2026-11-19T08:30:00.5+05:00" (WinRM).</summary>
    public static DateTime? DateTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        s = s.Trim();
        if (s.Length >= 21 && s[14] == '.' && char.IsDigit(s[0]))
        {
            try
            {
                return ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.UtcDateTime
            : null;
    }

    /// <summary>Converts a DCOM property value to invariant text.</summary>
    internal static string? FromObject(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        Array a => string.Join(",", a.Cast<object?>().Select(FromObject)),
        _ => value.ToString(),
    };

    /// <summary>Converts one WS-Management item (a WMI instance or method output as XML) to a row.</summary>
    public static WmiRow FromXml(string xml)
    {
        var row = new WmiRow();
        var root = XElement.Parse(xml);
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        foreach (var e in root.Elements())
        {
            var name = e.Name.LocalName;
            if ((string?)e.Attribute(xsi + "nil") == "true")
            {
                row.TryAdd(name, null);
                continue;
            }

            // Datetimes come as <p:LastBootUpTime><cim:Datetime>...</cim:Datetime></p:LastBootUpTime>.
            var value = e.HasElements ? e.Elements().First().Value : e.Value;
            if (row.TryGetValue(name, out var existing) && existing is not null)
            {
                row[name] = existing + "," + value; // arrays are repeated elements
            }
            else
            {
                row[name] = value;
            }
        }

        return row;
    }
}

/// <summary>WMI over DCOM (System.Management). Works without WinRM but needs RPC/DCOM through firewalls.</summary>
public sealed class DcomWmiSession : IWmiSession
{
    private readonly string _host;
    private readonly ConnectionOptions _options;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, ManagementScope> _scopes = new(StringComparer.OrdinalIgnoreCase);

    public DcomWmiSession(string host, WindowsAccount? account, TimeSpan timeout)
    {
        _host = LocalHost.Is(host) ? "." : host;
        _timeout = timeout;
        _options = new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            Timeout = timeout,
            EnablePrivileges = false,
        };

        // WMI refuses explicit credentials for local connections; the service account is used instead.
        if (account is { } a && _host != ".")
        {
            _options.Username = a.QualifiedUser;
            _options.SecurePassword = a.SecurePassword();
        }
    }

    public ServerCollectionMethod Method => ServerCollectionMethod.Dcom;

    private ManagementScope Scope(string wmiNamespace)
    {
        var ns = wmiNamespace.Replace('/', '\\');
        if (!_scopes.TryGetValue(ns, out var scope))
        {
            scope = new ManagementScope($@"\\{_host}\{ns}", _options);
            scope.Connect();
            _scopes[ns] = scope;
        }

        return scope;
    }

    public List<WmiRow> Query(string wmiNamespace, string wql)
    {
        var options = new System.Management.EnumerationOptions { Timeout = _timeout, ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(Scope(wmiNamespace), new ObjectQuery(wql), options);
        var rows = new List<WmiRow>();
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                var row = new WmiRow();
                foreach (var p in item.Properties)
                {
                    row[p.Name] = WmiValue.FromObject(p.Value);
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    public WmiRow Invoke(string wmiNamespace, string className, string method, IReadOnlyList<(string Name, object Value)> parameters)
    {
        using var cls = new ManagementClass(Scope(wmiNamespace), new ManagementPath(className), new ObjectGetOptions { Timeout = _timeout });
        using var inParams = cls.GetMethodParameters(method);
        foreach (var (name, value) in parameters)
        {
            inParams[name] = value;
        }

        using var outParams = cls.InvokeMethod(method, inParams, new InvokeMethodOptions { Timeout = _timeout });
        var row = new WmiRow();
        if (outParams is not null)
        {
            foreach (var p in outParams.Properties)
            {
                row[p.Name] = WmiValue.FromObject(p.Value);
            }
        }

        return row;
    }

    public void Dispose()
    {
        _scopes.Clear();
    }
}

/// <summary>
/// WMI over WinRM (WS-Management) using the Windows WinRM client (WSMan.Automation COM object). The WinRM client
/// handles Kerberos/NTLM authentication and message encryption, so HTTP (5985) traffic is encrypted too.
/// </summary>
public sealed class WinRmWmiSession : IWmiSession
{
    private const string WqlDialect = "http://schemas.microsoft.com/wbem/wsman/1/WQL";
    private const string WmiBase = "http://schemas.microsoft.com/wbem/wsman/1/wmi/";

    private readonly dynamic _session;

    public WinRmWmiSession(string host, int port, bool https, WindowsAccount? account, TimeSpan timeout)
    {
        var type = Type.GetTypeFromProgID("WSMan.Automation", throwOnError: false)
                   ?? throw new InvalidOperationException("The WinRM client (WSMan.Automation) is not available on this server.");
        dynamic wsman = Activator.CreateInstance(type)!;
        int flags = wsman.SessionFlagUTF8();
        dynamic? options = null;
        if (account is { } a && !LocalHost.Is(host))
        {
            options = wsman.CreateConnectionOptions();
            options.UserName = a.QualifiedUser;
            options.Password = a.Password;
            flags |= wsman.SessionFlagCredUsernamePassword() | wsman.SessionFlagUseNegotiate();
        }

        var scheme = https ? "https" : "http";
        var target = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        _session = options is null
            ? wsman.CreateSession($"{scheme}://{target}:{port}/wsman", flags)
            : wsman.CreateSession($"{scheme}://{target}:{port}/wsman", flags, options);
        _session.Timeout = (int)timeout.TotalMilliseconds;
    }

    public ServerCollectionMethod Method => ServerCollectionMethod.WinRm;

    public List<WmiRow> Query(string wmiNamespace, string wql)
    {
        var rows = new List<WmiRow>();
        dynamic enumerator = _session.Enumerate($"{WmiBase}{wmiNamespace}/*", wql, WqlDialect);
        while (!(bool)enumerator.AtEndOfStream)
        {
            string xml = enumerator.ReadItem();
            rows.Add(WmiValue.FromXml(xml));
        }

        return rows;
    }

    public WmiRow Invoke(string wmiNamespace, string className, string method, IReadOnlyList<(string Name, object Value)> parameters)
    {
        var uri = $"{WmiBase}{wmiNamespace}/{className}";
        XNamespace p = uri;
        var input = new XElement(p + $"{method}_INPUT",
            new XAttribute(XNamespace.Xmlns + "p", uri),
            parameters.Select(x => new XElement(p + x.Name, WmiValue.FromObject(x.Value))));
        string output = _session.Invoke(method, uri, input.ToString(SaveOptions.DisableFormatting));
        return WmiValue.FromXml(output);
    }

    public void Dispose()
    {
        if (System.Runtime.InteropServices.Marshal.IsComObject(_session))
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_session);
        }
    }
}
