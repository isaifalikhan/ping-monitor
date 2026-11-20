using System.Collections.Concurrent;
using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using NetWatch.Collectors.Polling;
using NetWatch.Core;

namespace NetWatch.Collectors.Snmp;

/// <summary>SNMP failure (timeout, authentication, protocol error).</summary>
public sealed class SnmpException : Exception
{
    public SnmpException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>One varbind of a walk: full OID, index suffix (after the walked column) and value.</summary>
public readonly record struct SnmpRow(string Oid, string Index, ISnmpData Data);

/// <summary>
/// SNMP v2c / v3 client for one target (GET and GETBULK walks) built on Lextm.SharpSnmpLib.
/// v3 engine discovery reports are cached per target and refreshed when the agent rejects them.
/// </summary>
public sealed class SnmpClient
{
    private const int MaxRepetitions = 25;
    private static readonly ConcurrentDictionary<string, (ReportMessage Report, DateTime FetchedUtc)> ReportCache = new();

    private readonly IPEndPoint _endpoint;
    private readonly SnmpCredential _credential;
    private readonly int _timeoutMs;
    private readonly int _retries;
    private readonly IPrivacyProvider? _privacy;

    public SnmpClient(IPAddress address, SnmpCredential credential, int timeoutMs, int retries, int port = 161)
    {
        _endpoint = new IPEndPoint(address, port);
        _credential = credential;
        _timeoutMs = Math.Max(200, timeoutMs);
        _retries = Math.Max(0, retries);
        if (credential.Kind == CredentialKind.SnmpV3)
        {
            _privacy = BuildPrivacy(credential.Secret);
        }
    }

    public IPEndPoint Endpoint => _endpoint;

    /// <summary>GET of scalar OIDs. Missing objects (noSuchObject/noSuchInstance) are omitted from the result.</summary>
    public async Task<Dictionary<string, ISnmpData>> GetAsync(IReadOnlyList<string> oids, CancellationToken ct)
    {
        var variables = oids.Select(o => new Variable(new ObjectIdentifier(o))).ToList();
        var response = await SendAsync(isBulk: false, variables, ct);
        var result = new Dictionary<string, ISnmpData>(StringComparer.Ordinal);
        foreach (var v in response)
        {
            if (!IsException(v.Data))
            {
                result[v.Id.ToString()] = v.Data;
            }
        }

        return result;
    }

    /// <summary>Walks a table column (or subtree) with GETBULK.</summary>
    /// <param name="rootOid">Column OID without trailing dot.</param>
    /// <param name="maxRows">Safety limit against agents that loop.</param>
    public async Task<List<SnmpRow>> WalkAsync(string rootOid, CancellationToken ct, int maxRows = 10_000)
    {
        var prefix = rootOid + ".";
        var rows = new List<SnmpRow>();
        var current = rootOid;
        while (rows.Count < maxRows)
        {
            ct.ThrowIfCancellationRequested();
            var response = await SendAsync(isBulk: true, [new Variable(new ObjectIdentifier(current))], ct);
            if (response.Count == 0)
            {
                break;
            }

            var done = false;
            foreach (var v in response)
            {
                var oid = v.Id.ToString();
                if (v.Data.TypeCode == SnmpType.EndOfMibView || !oid.StartsWith(prefix, StringComparison.Ordinal))
                {
                    done = true;
                    break;
                }

                if (!IsException(v.Data))
                {
                    rows.Add(new SnmpRow(oid, oid[prefix.Length..], v.Data));
                }

                current = oid;
            }

            if (done)
            {
                break;
            }
        }

        return rows;
    }

    /// <summary>Walks a column and returns index → value.</summary>
    public async Task<Dictionary<string, ISnmpData>> WalkColumnAsync(string column, CancellationToken ct)
    {
        var rows = await WalkAsync(column, ct);
        var map = new Dictionary<string, ISnmpData>(rows.Count, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            map[r.Index] = r.Data;
        }

        return map;
    }

    private async Task<IList<Variable>> SendAsync(bool isBulk, IList<Variable> variables, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= _retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return _credential.Kind == CredentialKind.SnmpV3
                    ? await SendV3Async(isBulk, variables, ct, attempt > 0)
                    : await SendV2Async(isBulk, variables, ct);
            }
            catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or System.TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
            catch (ErrorException ex)
            {
                throw new SnmpException($"SNMP error from agent: {ex.Body?.Pdu().ErrorStatus}", ex);
            }
            catch (SnmpException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Usually an authentication/decryption failure (wrong v3 keys) or a malformed packet.
                throw new SnmpException(ex.Message, ex);
            }
        }

        throw new SnmpException($"No SNMP response from {_endpoint.Address} within {_timeoutMs} ms", last);
    }

    private async Task<IList<Variable>> SendV2Async(bool isBulk, IList<Variable> variables, CancellationToken ct)
    {
        var community = new OctetString(_credential.Secret.Community ?? string.Empty);
        ISnmpMessage request = isBulk
            ? new GetBulkRequestMessage(Messenger.NextRequestId, VersionCode.V2, community, 0, MaxRepetitions, variables)
            : new GetRequestMessage(Messenger.NextRequestId, VersionCode.V2, community, variables);
        var response = await ExchangeAsync(request, ct);
        return Check(response);
    }

    private async Task<IList<Variable>> SendV3Async(bool isBulk, IList<Variable> variables, CancellationToken ct, bool refreshReport)
    {
        var report = await GetReportAsync(ct, refreshReport);
        var user = new OctetString(_credential.Secret.Username ?? string.Empty);
        ISnmpMessage request = BuildV3(isBulk, user, variables, report);
        var response = await ExchangeAsync(request, ct);

        if (response is ReportMessage)
        {
            // Engine boots/time out of window or unknown engine ID: rediscover once.
            ReportCache.TryRemove(CacheKey, out _);
            report = await GetReportAsync(ct, refresh: true);
            request = BuildV3(isBulk, user, variables, report);
            response = await ExchangeAsync(request, ct);
            if (response is ReportMessage rejected)
            {
                throw new SnmpException($"SNMPv3 request rejected by agent ({DescribeReport(rejected)}). Check user name, protocols and keys.");
            }
        }

        return Check(response);
    }

    private ISnmpMessage BuildV3(bool isBulk, OctetString user, IList<Variable> variables, ReportMessage report) => isBulk
        ? new GetBulkRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user, OctetString.Empty, 0, MaxRepetitions, variables, _privacy!, Messenger.MaxMessageSize, report)
        : new GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user, OctetString.Empty, variables, _privacy!, Messenger.MaxMessageSize, report);

    private string CacheKey =>$"{_endpoint}|{_credential.Secret.Username}";

    private async Task<ReportMessage> GetReportAsync(CancellationToken ct, bool refresh)
    {
        if (!refresh && ReportCache.TryGetValue(CacheKey, out var cached) && DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromMinutes(10))
        {
            return cached.Report;
        }

        var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        var report = await Task.Run(() => discovery.GetResponse(_timeoutMs, _endpoint), ct).WaitAsync(TimeSpan.FromMilliseconds(_timeoutMs + 1000), ct);
        ReportCache[CacheKey] = (report, DateTime.UtcNow);
        return report;
    }

    private async Task<ISnmpMessage> ExchangeAsync(ISnmpMessage request, CancellationToken ct)
    {
        // The library's synchronous API takes an explicit timeout; run it off the caller's thread.
        var registry = _privacy is null ? new UserRegistry() : RegistryFor(_privacy);
        return await Task.Run(() => request.GetResponse(_timeoutMs, _endpoint, registry), ct)
            .WaitAsync(TimeSpan.FromMilliseconds(_timeoutMs + 1000), ct);
    }

    private UserRegistry RegistryFor(IPrivacyProvider privacy)
    {
        var registry = new UserRegistry();
        registry.Add(new OctetString(_credential.Secret.Username ?? string.Empty), privacy);
        return registry;
    }

    private static IList<Variable> Check(ISnmpMessage response)
    {
        var pdu = response.Pdu();
        var status = pdu.ErrorStatus.ToInt32();
        if (status != 0)
        {
            throw new SnmpException($"SNMP error status {(ErrorCode)status} at index {pdu.ErrorIndex.ToInt32()}");
        }

        return pdu.Variables;
    }

    private static bool IsException(ISnmpData data) =>
        data.TypeCode is SnmpType.NoSuchInstance or SnmpType.NoSuchObject or SnmpType.EndOfMibView or SnmpType.Null;

    private static string DescribeReport(ReportMessage report)
    {
        var oid = report.Pdu().Variables.FirstOrDefault()?.Id.ToString() ?? string.Empty;
        return oid switch
        {
            "1.3.6.1.6.3.15.1.1.3.0" => "unknown user name",
            "1.3.6.1.6.3.15.1.1.5.0" => "wrong authentication password/protocol",
            "1.3.6.1.6.3.15.1.1.6.0" => "decryption error: wrong privacy password/protocol",
            "1.3.6.1.6.3.15.1.1.1.0" => "unsupported security level",
            "1.3.6.1.6.3.15.1.1.2.0" => "not in time window",
            "1.3.6.1.6.3.15.1.1.4.0" => "unknown engine ID",
            _ => $"report {oid}",
        };
    }

    // MD5, SHA-1 and DES are weak, but many installed switches (older Cisco IOS, HP ProCurve) only offer them.
    // They stay selectable so those devices can be monitored; the credential UI recommends SHA-256 + AES.
#pragma warning disable CS0618
    internal static IPrivacyProvider BuildPrivacy(Core.Credentials.CredentialSecret s)
    {
        IAuthenticationProvider auth = s.AuthProtocol switch
        {
            SnmpAuthProtocol.None => DefaultAuthenticationProvider.Instance,
            SnmpAuthProtocol.MD5 => new MD5AuthenticationProvider(new OctetString(s.AuthPassword ?? string.Empty)),
            SnmpAuthProtocol.SHA1 => new SHA1AuthenticationProvider(new OctetString(s.AuthPassword ?? string.Empty)),
            SnmpAuthProtocol.SHA256 => new SHA256AuthenticationProvider(new OctetString(s.AuthPassword ?? string.Empty)),
            SnmpAuthProtocol.SHA384 => new SHA384AuthenticationProvider(new OctetString(s.AuthPassword ?? string.Empty)),
            SnmpAuthProtocol.SHA512 => new SHA512AuthenticationProvider(new OctetString(s.AuthPassword ?? string.Empty)),
            _ => throw new SnmpException($"Unsupported authentication protocol {s.AuthProtocol}"),
        };

        var priv = new OctetString(s.PrivPassword ?? string.Empty);
        return s.PrivProtocol switch
        {
            SnmpPrivProtocol.None => new DefaultPrivacyProvider(auth),
            SnmpPrivProtocol.DES => new DESPrivacyProvider(priv, auth),
            SnmpPrivProtocol.AES128 => new AESPrivacyProvider(priv, auth),
            SnmpPrivProtocol.AES192 => new AES192PrivacyProvider(priv, auth),
            SnmpPrivProtocol.AES256 => new AES256PrivacyProvider(priv, auth),
            _ => throw new SnmpException($"Unsupported privacy protocol {s.PrivProtocol}"),
        };
    }
#pragma warning restore CS0618
}

/// <summary>Conversions from SNMP data types.</summary>
public static class SnmpValues
{
    public static ulong? UInt64(ISnmpData? d) => d switch
    {
        Counter64 c => c.ToUInt64(),
        Counter32 c => c.ToUInt32(),
        Gauge32 g => g.ToUInt32(),
        TimeTicks t => t.ToUInt32(),
        Integer32 i => i.ToInt32() >= 0 ? (ulong)i.ToInt32() : null,
        _ => null,
    };

    public static long? Int64(ISnmpData? d) => d switch
    {
        Integer32 i => i.ToInt32(),
        Counter64 c => c.ToUInt64() > long.MaxValue ? null : (long)c.ToUInt64(),
        Counter32 c => c.ToUInt32(),
        Gauge32 g => g.ToUInt32(),
        TimeTicks t => t.ToUInt32(),
        OctetString s when long.TryParse(s.ToString(), out var v) => v,
        _ => null,
    };

    public static double? Double(ISnmpData? d) => Int64(d);

    public static string? Text(ISnmpData? d)
    {
        if (d is null)
        {
            return null;
        }

        var text = d is OctetString s ? s.ToString() : d.ToString();
        text = text?.Replace("\0", string.Empty).Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static string? Mac(ISnmpData? d)
    {
        if (d is not OctetString s)
        {
            return null;
        }

        var bytes = s.GetRaw();
        return bytes.Length == 6 ? string.Join(':', bytes.Select(b => b.ToString("X2"))) : null;
    }
}
