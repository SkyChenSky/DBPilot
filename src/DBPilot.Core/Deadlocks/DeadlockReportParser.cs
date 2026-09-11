using System.Xml.Linq;
using DBPilot.Common;

namespace DBPilot.Core.Deadlocks;

/// <summary>
/// 死锁 XML 解析（平台侧纯函数）：
/// XE event_data → 死锁模型（进程 / 资源 / 持有-申请边 / victim / 指纹）。
/// event timestamp 兼容 ISO 8601（event_file / ring_buffer 均为此格式）与 1601-01-01 起 64bit 微秒两种形态。
/// 进程属性中的时间是实例本地时间，由调用方传 offset（SYSDATETIME - SYSUTCDATETIME）转 UTC。
/// </summary>
public static class DeadlockReportParser
{
    /// <summary>XE event_data XML → 死锁模型；event 名非 xml_deadlock_report 返回 null。</summary>
    public static DeadlockEventModel? ParseEvent(string eventXml, TimeSpan localToUtcOffset)
    {
        var root = XElement.Parse(eventXml);
        if (root.Attribute("name")?.Value != "xml_deadlock_report") return null;

        var timeUtc = ParseTimestamp(root.Attribute("timestamp")?.Value)
                      ?? throw new FormatException("XE event 缺少 timestamp 属性");
        var valueEl = root.Elements("data")
            .FirstOrDefault(d => (string?)d.Attribute("name") == "xml_report")?.Element("value");
        if (valueEl is null) return null;

        // value 内为转义文本（fn_xe_file_target_read_file 真实形态）或内联 XML 节点（ring_buffer target_data 形态）
        var graph = valueEl.HasElements ? valueEl.Elements().First().ToString() : valueEl.Value;
        if (string.IsNullOrWhiteSpace(graph)) return null;

        return Parse(graph, timeUtc, localToUtcOffset);
    }

    /// <summary>&lt;deadlock&gt; XML → 死锁模型（已知事件时刻时直接解析图）。</summary>
    public static DeadlockEventModel Parse(string deadlockXml, DateTime eventTimeUtc, TimeSpan localToUtcOffset)
    {
        var root = XElement.Parse(deadlockXml);
        var victims = root.Element("victim-list")?.Elements("victimProcess")
            .Select(v => (string?)v.Attribute("id") ?? "").Where(s => s.Length > 0).ToList() ?? [];

        var processes = root.Element("process-list")?.Elements("process").Select(p => new DeadlockProcess
        {
            Id = (string?)p.Attribute("id") ?? "",
            Spid = (int?)p.Attribute("spid") ?? 0,
            IsVictim = victims.Contains((string?)p.Attribute("id") ?? ""),
            LoginName = (string?)p.Attribute("loginname"),
            HostName = (string?)p.Attribute("hostname"),
            ClientApp = (string?)p.Attribute("clientapp"),
            IsolationLevel = (string?)p.Attribute("isolationlevel"),
            LockMode = (string?)p.Attribute("lockMode"),
            WaitResource = (string?)p.Attribute("waitresource"),
            Status = (string?)p.Attribute("status"),
            TransactionName = (string?)p.Attribute("transactionname"),
            CurrentDatabaseId = (int?)p.Attribute("currentdatabase") ?? 0,
            Trancount = (int?)p.Attribute("trancount") ?? 0,
            LogUsed = (int?)p.Attribute("logused") ?? 0,
            WaitTimeMs = (int?)p.Attribute("waittime") ?? 0,
            TaskPriority = (int?)p.Attribute("taskpriority") ?? 0,
            LastTranStartedUtc = ToUtc((string?)p.Attribute("lasttranstarted"), localToUtcOffset),
            LastBatchStartedUtc = ToUtc((string?)p.Attribute("lastbatchstarted"), localToUtcOffset),
            LastBatchCompletedUtc = ToUtc((string?)p.Attribute("lastbatchcompleted"), localToUtcOffset),
            InputBuf = (string?)p.Element("inputbuf"),
            ExecutionStack = p.Element("executionStack")?.Elements("frame")
                .Take(5)
                .Select(f => ((string?)f.Attribute("procname") ?? "adhoc") + ": " + (f.Value ?? "").Trim())
                .ToList() ?? [],
        }).ToList() ?? [];

        var resources = root.Element("resource-list")?.Elements().Select(ToResource).ToList() ?? [];

        var model = new DeadlockEventModel
        {
            EventTimeUtc = eventTimeUtc,
            VictimProcessIds = victims,
            Processes = processes,
            Resources = resources,
            GraphXml = deadlockXml,
            Fingerprint = ComputeFingerprint(processes, resources),
        };
        return model;
    }

    /// <summary>指纹：SHA256 前 16 hex，输入 = 排序的资源描述行 + 边行（进程规范编号 + 角色 + 资源描述 + 模式）。
    /// 进程 id（processXXXXXXXX）与锁 id（lockXXXXXXXX）每次发生都不同，一律不进指纹 —— 同构图归并去重；
    /// 进程规范编号 = 其出入边签名（角色+资源描述+模式集合）的确定性哈希（string.GetHashCode 跨进程不稳定，禁用）。</summary>
    public static string ComputeFingerprint(List<DeadlockProcess> processes, List<DeadlockResource> resources)
    {
        // 锁 id → 资源描述行（id 可能缺失/重复，GroupBy 收敛）
        var desc = resources.GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => $"{g.First().ResourceType}:{g.First().Display}:{g.First().Mode}");

        var canonical = new Dictionary<string, string>();
        foreach (var p in processes)
        {
            var sig = string.Join(",",
                resources.SelectMany(r => r.Owners.Where(o => o.ProcessId == p.Id)
                        .Select(o => $"O:{desc[r.Id]}:{o.Mode}"))
                    .Concat(resources.SelectMany(r => r.Waiters.Where(w => w.ProcessId == p.Id)
                        .Select(w => $"W:{desc[r.Id]}:{w.Mode}")))
                    .OrderBy(s => s));
            canonical[p.Id] = "p" + sig.Sha256PrefixHex();
        }

        var parts = new List<string>();
        foreach (var r in resources)
        {
            parts.Add($"R:{desc[r.Id]}");
            foreach (var o in r.Owners)
                parts.Add($"E:{canonical.GetValueOrDefault(o.ProcessId, "?")}:O:{desc[r.Id]}:{o.Mode}");
            foreach (var w in r.Waiters)
                parts.Add($"E:{canonical.GetValueOrDefault(w.ProcessId, "?")}:W:{desc[r.Id]}:{w.Mode}");
        }

        var normalized = string.Join("|", parts.OrderBy(s => s, StringComparer.Ordinal));
        return normalized.Sha256PrefixHex();
    }

    private static DeadlockResource ToResource(XElement e)
    {
        var name = (string?)e.Attribute("objectname");
        var index = (string?)e.Attribute("indexname");
        var type = e.Name.LocalName;

        string display;
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(index))
            display = $"{name}（{index}）";
        else if (!string.IsNullOrEmpty(name))
            display = name!;
        else if (type == "exchangeEvent")
            display = "并行交换（Parallelism Exchange）";
        else if (type == "threadlock")
            display = "线程（threadlock）";
        else
        {
            // 其余类型取关键属性拼摘要（如 metadatalock / memorylock / synchlock）
            var attrs = e.Attributes()
                .Where(a => a.Name.LocalName is not ("id" or "mode"))
                .Take(4)
                .Select(a => $"{a.Name.LocalName}={a.Value}");
            display = $"{type}: {string.Join(",", attrs)}";
        }

        return new DeadlockResource
        {
            Id = (string?)e.Attribute("id") ?? "",
            ResourceType = type,
            Display = display,
            ObjectName = name,
            IndexName = index,
            DatabaseId = (string?)e.Attribute("databaseid"),
            Mode = (string?)e.Attribute("mode"),
            Owners = e.Element("owner-list")?.Elements("owner").Select(o => new DeadlockProcessRef
            {
                ProcessId = (string?)o.Attribute("id") ?? "",
                Mode = (string?)o.Attribute("mode") ?? "",
            }).ToList() ?? [],
            Waiters = e.Element("waiter-list")?.Elements("waiter").Select(w => new DeadlockProcessRef
            {
                ProcessId = (string?)w.Attribute("id") ?? "",
                Mode = (string?)w.Attribute("mode") ?? "",
            }).ToList() ?? [],
        };
    }

    /// <summary>XE event timestamp：ISO 8601（event_file/ring_buffer）或 1601-01-01 起 64bit 微秒（旧形态兼容）。</summary>
    public static DateTime? ParseTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (long.TryParse(value, out var micros))
            return new DateTime(micros * 10, DateTimeKind.Utc);   // µs → 100ns ticks
        if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return dt.ToUniversalTime();
        }
        return null;
    }

    /// <summary>死锁 XML 内的本地时间属性 → UTC（offset = SYSDATETIME - SYSUTCDATETIME）。</summary>
    private static DateTime? ToUtc(string? value, TimeSpan offset)
        => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.None, out var dt)
            ? DateTime.SpecifyKind(dt - offset, DateTimeKind.Utc)
            : null;
}
