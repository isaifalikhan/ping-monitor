using System.Text;
using NetWatch.Core;
using NetWatch.Core.Entities;
using NetWatch.Web.Api;

namespace NetWatch.Tests;

/// <summary>WP4: DC security event CSV export and the stored port-check JSON shown on the DC cards.</summary>
public sealed class Wp4DcUiTests
{
    [Fact]
    public void Csv_export_has_headers_pkt_times_decoded_results_and_is_formula_safe()
    {
        var rows = new List<DcSecurityEvent>
        {
            new()
            {
                Id = 1, DeviceId = 3, EventId = 4625, RecordId = 9, Severity = Severity.Warning, Category = "LogonFailure",
                TimestampUtc = new DateTime(2026, 11, 18, 7, 42, 13, DateTimeKind.Utc), TargetUser = "=HYPERLINK(\"http://x\")", TargetDomain = null,
                SourceIp = "10.10.31.77", Workstation = "WS-FIN-07", LogonType = 3, Status = "0xc000006d", SubStatus = "0xc000006a",
                Message = "Failed logon: CORP\\bilal.ahmed from 10.10.31.77",
            },
            new() { Id = 2, DeviceId = 3, EventId = 1102, RecordId = 10, Severity = Severity.Critical, Category = "Policy", TimestampUtc = DateTime.UtcNow, SubjectUser = "@adm", Message = "-cleared" },
        };

        var bytes = DcEndpoints.WriteCsv(rows, new Dictionary<int, string> { [3] = "DC01" });
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // UTF-8 BOM for Excel
        var text = Encoding.UTF8.GetString(bytes[3..]);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Time (PKT),DC,Event ID,Event,Category,Severity,Subject,Target", lines[0]);
        Assert.StartsWith("2026-11-18 12:42:13,DC01,4625,", lines[1]);          // UTC+5
        Assert.Contains("wrong password", lines[1]);                            // sub-status decoded
        Assert.Contains("'=HYPERLINK", lines[1]);                               // no live formula
        Assert.DoesNotContain(",=HYPERLINK", lines[1]);
        Assert.Contains(",'@adm,", lines[2]);
        Assert.Contains(",'-cleared", lines[2]);
    }

    [Fact]
    public void Port_json_round_trips_and_tolerates_garbage()
    {
        var ports = DcEndpoints.ParsePorts("""[{"service":"LDAP","port":389,"error":null,"ms":0.8},{"service":"DNS","port":53,"error":"connection refused","ms":null}]""");
        Assert.Equal(2, ports.Count);
        Assert.Equal(("LDAP", 389, (string?)null, (double?)0.8), (ports[0].Service, ports[0].Port, ports[0].Error, ports[0].Ms));
        Assert.Equal("connection refused", ports[1].Error);
        Assert.Empty(DcEndpoints.ParsePorts(null));
        Assert.Empty(DcEndpoints.ParsePorts("{not json"));
        Assert.Empty(DcEndpoints.ParsePorts("""[{"port":1}]"""));
    }

    [Fact]
    public void Export_is_capped() => Assert.Equal(100_000, DcEndpoints.MaxExportRows);
}
