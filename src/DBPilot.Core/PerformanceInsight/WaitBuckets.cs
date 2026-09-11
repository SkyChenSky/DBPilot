namespace DBPilot.Core.PerformanceInsight;

/// <summary>
/// 等待分桶映射：20 桶，按微软官方等待体系（sys.dm_os_wait_stats /
/// Paul Randal 有意义等待清单）归并原生 Wait Types；未命中一律归"其他"（禁止丢弃，R3 防御）。
/// WAITFOR（用户显式延迟）计活跃、单独归 userWait 桶（对齐阿里云 User Sleep 会话占用口径）；
/// SLEEP 等引擎后台空闲归 idle 桶——保留分类但不计 AAS（SampleTickBuilder 排除）。
/// </summary>
public static class WaitBuckets
{
    public const string Cpu = "cpu";
    public const string Lock = "lock";
    public const string UserIo = "userIo";
    public const string LogWrite = "logWrite";
    public const string SysIo = "sysIo";
    public const string BufferLatch = "bufferLatch";
    public const string Latch = "latch";
    public const string Network = "network";
    public const string Memory = "memory";
    public const string Parallel = "parallel";
    public const string Threads = "threads";
    public const string Backup = "backup";
    public const string Hadr = "hadr";
    public const string Trace = "trace";
    public const string Broker = "broker";
    public const string FullText = "fullText";
    public const string Preemptive = "preemptive";
    public const string UserWait = "userWait";
    public const string Idle = "idle";
    public const string Other = "other";

    /// <summary>全部桶（固定顺序，前端堆叠面积图图层序；高频类别在前）。</summary>
    public static readonly string[] All =
    [
        Cpu, Lock, UserIo, LogWrite, SysIo, BufferLatch, Latch, Network, Memory, Parallel,
        Threads, Backup, Hadr, Trace, Broker, FullText, Preemptive, UserWait, Idle, Other,
    ];

    /// <summary>桶 → 中文提示名（API 下发前端 tooltip；图例直接用桶 key，本身即英文）。</summary>
    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        [Cpu] = "CPU",
        [Lock] = "锁等待",
        [UserIo] = "用户 IO（读）",
        [LogWrite] = "日志写",
        [SysIo] = "系统 IO",
        [BufferLatch] = "缓冲闩锁",
        [Latch] = "闩锁",
        [Network] = "网络（等客户端）",
        [Memory] = "内存",
        [Parallel] = "并行",
        [Threads] = "工作线程耗尽",
        [Backup] = "备份",
        [Hadr] = "高可用/复制",
        [Trace] = "追踪/扩展事件",
        [Broker] = "Service Broker",
        [FullText] = "全文检索",
        [Preemptive] = "抢占式调用",
        [UserWait] = "用户等待（WAITFOR）",
        [Idle] = "引擎后台空闲（不计 AAS）",
        [Other] = "其他",
    };

    /// <summary>引擎后台/空闲循环等待 —— 归 idle 桶（保留分类，不计 AAS；先于前缀族判断）。</summary>
    private static readonly HashSet<string> Ignored =
        ["SLEEP", "BROKER_TASK_WAIT", "DIRTY_PAGE_POLL", "HADR_FILESTREAM_IOMGR"];

    /// <summary>
    /// 样本（status + waitType）→ 桶；返回 null = 无对应桶，样本排除。
    /// running/runnable 无等待 → CPU；suspended 按等待类型分桶。
    /// </summary>
    public static string? BucketOf(string? status, string? waitType)
    {
        if (string.IsNullOrWhiteSpace(waitType)) return Cpu;   // running / runnable
        var w = waitType.Trim().ToUpperInvariant();

        if (Ignored.Contains(w)) return Idle;
        if (w == "SOS_SCHEDULER_YIELD") return Cpu;
        if (w == "WAITFOR") return UserWait;
        if (w.StartsWith("PAGEIOLATCH_") || w == "DISKIO") return UserIo;
        if (w is "WRITELOG" or "LOGBUFFER" || w.StartsWith("LOGMGR_")) return LogWrite;
        if (w == "IO_COMPLETION") return SysIo;
        if (w.StartsWith("LCK_M_")) return Lock;
        if (w.StartsWith("PAGELATCH_")) return BufferLatch;
        if (w.StartsWith("LATCH_") || w.StartsWith("ACCESS_METHODS_")) return Latch;
        if (w is "ASYNC_NETWORK_IO" or "NETWORK_IDMGR" or "NET_WAITFOR_PACKET") return Network;
        if (w is "RESOURCE_SEMAPHORE" or "RESOURCE_SEMAPHORE_QUERY_COMPILE"
            or "MEMORY_ALLOCATION_EXT" or "CMEMTHREAD") return Memory;
        if (w is "CXPACKET" or "CXCONSUMER") return Parallel;
        if (w == "THREADPOOL") return Threads;
        if (w.StartsWith("BACKUP")) return Backup;
        if (w.StartsWith("HADR_") || w.StartsWith("DBMIRROR") || w.StartsWith("SE_REPL_")
            || w.StartsWith("REPL_") || w.StartsWith("MIRROR_")) return Hadr;
        if (w.StartsWith("XE_") || w.StartsWith("SQLTRACE_") || w == "TRACEWRITE" || w.StartsWith("QDS_")) return Trace;
        if (w.StartsWith("BROKER_")) return Broker;
        if (w.StartsWith("MSSEARCH") || w.StartsWith("FULLTEXT_")) return FullText;
        if (w.StartsWith("PREEMPTIVE_")) return Preemptive;
        // MySQL 会话等待状态（MySqlProvider 由 data_lock_waits 成员 / PROCESSLIST_STATE 派生；
        // 词汇与 SQL Server wait_type 是两套体系，"WAITING FOR" 前缀不会撞上表）：
        // 行锁 / 元数据锁 / 表级锁等待归 Lock；其余 Waiting for…（如 Waiting for table flush）未命中走 Other
        if (w.StartsWith("WAITING FOR") && w.Contains("LOCK")) return Lock;
        return Other;
    }
}
