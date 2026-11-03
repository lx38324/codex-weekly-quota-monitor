using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace WeeklyQuotaMonitor;

/// <summary>遥测独立保留策略；空间预算包含数据库及回滚日志，不影响额度和 JSONL 历史。</summary>
public sealed class TelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 4318;
    public int DetailDays { get; set; } = 7;
    public int MinuteDays { get; set; } = 30;
    public int RetentionDays { get; set; } = 365;
    public int MaximumMegabytes { get; set; } = 256;
    /// <summary>验证端口与递进保留窗口，给 SQLite、请求缓冲留出独立安全边界。</summary>
    public bool IsValid() => Port is >= 1024 and <= 65535 && DetailDays is >= 1 and <= 30 &&
        MinuteDays >= DetailDays && MinuteDays <= 90 && RetentionDays >= MinuteDays && RetentionDays <= 730 &&
        MaximumMegabytes is >= 16 and <= 1024;
}

/// <summary>一个导出区间或降采样桶的数值摘要，不是一次请求；单位固定为毫秒。</summary>
public sealed record TelemetryBucket(DateTimeOffset Start, DateTimeOffset End, string Metric, string Model,
    string Tier, long Count, double Sum, int ResolutionSeconds, string TierSource = "metric",
    string LocalModel = "", string Origin = "unknown", string Association = "unmatched", string MatchBasis = "none", long ModelMismatches = 0)
{
    public double Mean => Sum / Count;
}
public sealed record TelemetryQuery(IReadOnlyList<TelemetryBucket> Buckets, string[] Models,
    DateTimeOffset? First, DateTimeOffset? Last, bool Truncated)
{
    public IReadOnlyList<TelemetryDiagnosticSummary> Diagnostics { get; init; } = [];
}

/// <summary>序列差分和数值摘要在同一 SQLite 事务中提交；禁止把累计快照相加或将基线重置伪装成新观测。</summary>
public sealed partial class TelemetryStore
{
    private readonly string _path;
    private long _maximumPages;
    public long Baselines { get; private set; }
    public long Duplicates { get; private set; }
    public long Discontinuities { get; private set; }
    public long EvictedRows { get; private set; }

    /// <summary>创建独立数据库；数据库占预算 49%，为同量 journal 及页头额外开销保留余量。</summary>
    public TelemetryStore(string path, TelemetryOptions options)
    {
        _path = path;
        _maximumPages = options.MaximumMegabytes * 1024L * 1024 * 49 / 100 / 4096;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var db = Open();
        if (Scalar(db, "PRAGMA user_version") > 2) throw new InvalidDataException("Unsupported telemetry database version");
        Execute(db, "PRAGMA page_size=4096; PRAGMA auto_vacuum=FULL; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;" +
            "CREATE TABLE IF NOT EXISTS baseline (id TEXT PRIMARY KEY, end_ns INTEGER NOT NULL, payload TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS samples (start_ms INTEGER NOT NULL,end_ms INTEGER NOT NULL,metric TEXT NOT NULL,model TEXT NOT NULL,tier TEXT NOT NULL,n INTEGER NOT NULL,total REAL NOT NULL,res INTEGER NOT NULL,bounds TEXT NOT NULL,counts TEXT NOT NULL, PRIMARY KEY(res,end_ms,metric,model,tier,bounds));" +
            "CREATE INDEX IF NOT EXISTS sample_time ON samples(end_ms);" +
            "CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY,value INTEGER NOT NULL);");
        InitializeRequests(db);
        InitializeDiagnostics(db);
        Maintain(options, DateTimeOffset.UtcNow);
    }

    /// <summary>每个连接限制缓存和临时存储；不使用连接池持有安装目录中文件。</summary>
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        db.Open(); Execute(db, $"PRAGMA busy_timeout=2000; PRAGMA cache_size=-2048; PRAGMA temp_store=MEMORY; PRAGMA max_page_count={_maximumPages};");
        return db;
    }

    /// <summary>处理顺序/重复/计数回退；首次累计仅设基线，未知历史时间分布不补在当前时刻。</summary>
    public void Append(IReadOnlyList<TelemetryPoint> points, DateTimeOffset receivedAt,
        IReadOnlyList<TelemetryDiagnosticEvent>? received = null)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var diagnostics = received?.ToList() ?? [];
        foreach (var p in points)
        {
            var eventAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(p.End / 1_000_000));
            var outcome = "empty"; var reason = "";
            if (p.End > (ulong)receivedAt.AddMinutes(5).ToUnixTimeMilliseconds() * 1_000_000UL)
                throw new InvalidDataException("Telemetry timestamp is in the future");
            using var find = db.CreateCommand(); find.Transaction = tx;
            find.CommandText = "SELECT payload FROM baseline WHERE id=$id"; find.Parameters.AddWithValue("$id", p.Series);
            var priorJson = find.ExecuteScalar() as string;
            var prior = priorJson is null ? null : JsonSerializer.Deserialize<TelemetryPoint>(priorJson)!;
            if (prior is not null && p.End <= prior.End)
            { Duplicates++; diagnostics.Add(new("metrics", p.Metric, p.Model, "duplicate", eventAt)); continue; }
            long count = p.Count; var sum = p.Sum; var counts = p.Counts; var start = p.Start;
            var accept = true;
            if (p.Temporality == 2)
            {
                if (prior is null) { Baselines++; accept = false; outcome = "baseline"; }
                else if (p.Count < prior.Count || p.Sum < prior.Sum || !p.Bounds.SequenceEqual(prior.Bounds) ||
                    p.Counts.Where((c, i) => c < prior.Counts[i]).Any())
                { Discontinuities++; accept = false; outcome = "discontinuity"; reason = $"cumulative changed: previous count={prior.Count}, current count={p.Count}"; }
                else
                {
                    count -= prior.Count; sum -= prior.Sum; start = prior.End;
                    counts = p.Counts.Select((c, i) => c - prior.Counts[i]).ToArray();
                }
            }
            else if (prior is not null && p.Start < prior.End)
            { Discontinuities++; accept = false; outcome = "discontinuity"; reason = "overlapping delta intervals"; }
            if (accept && count > 0)
            {
                Merge(db, tx, new Row((long)(start / 1_000_000), (long)(p.End / 1_000_000), p.Metric, p.Model, p.Tier,
                    count, sum, 0, JsonSerializer.Serialize(p.Bounds), counts));
                outcome = "stored";
            }
            diagnostics.Add(new("metrics", p.Metric, p.Model, outcome, eventAt, outcome == "stored" ? count : 0, reason));
            using var save = db.CreateCommand(); save.Transaction = tx;
            save.CommandText = "INSERT INTO baseline VALUES($id,$end,$json) ON CONFLICT(id) DO UPDATE SET end_ns=$end,payload=$json";
            save.Parameters.AddWithValue("$id", p.Series); save.Parameters.AddWithValue("$end", checked((long)p.End));
            save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(p)); save.ExecuteNonQuery();
        }
        WriteDiagnostics(db, tx, diagnostics, receivedAt);
        tx.Commit();
    }

    /// <summary>按相同数值桶边界合并摘要，保留桶计数，后续可计算分位数而非平均分位数。</summary>
    private static void Merge(SqliteConnection db, SqliteTransaction tx, Row row)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        Bind(cmd, row);
        cmd.CommandText = "SELECT counts FROM samples WHERE res=$r AND end_ms=$e AND metric=$m AND model=$model AND tier=$tier AND bounds=$b";
        var old = cmd.ExecuteScalar() as string;
        var counts = old is null ? row.Counts : JsonSerializer.Deserialize<long[]>(old)!.Select((n, i) => checked(n + row.Counts[i])).ToArray();
        cmd.Parameters.AddWithValue("$s", row.Start); cmd.Parameters.AddWithValue("$n", row.Count);
        cmd.Parameters.AddWithValue("$total", row.Sum); cmd.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(counts));
        cmd.CommandText = "INSERT INTO samples VALUES($s,$e,$m,$model,$tier,$n,$total,$r,$b,$counts) " +
            "ON CONFLICT(res,end_ms,metric,model,tier,bounds) DO UPDATE SET start_ms=min(start_ms,$s), n=n+$n,total=total+$total,counts=$counts";
        cmd.ExecuteNonQuery();
    }

    /// <summary>所有 SQL 参数集中绑定，模型名和外部字段从不插入 SQL 文本。</summary>
    private static void Bind(SqliteCommand cmd, Row row)
    {
        cmd.Parameters.AddWithValue("$r", row.Res); cmd.Parameters.AddWithValue("$e", row.End);
        cmd.Parameters.AddWithValue("$m", row.Metric); cmd.Parameters.AddWithValue("$model", row.Model);
        cmd.Parameters.AddWithValue("$tier", row.Tier); cmd.Parameters.AddWithValue("$b", row.Bounds);
    }

    /// <summary>按模型及明确速度模式限定导航边界和观测，再按结束时间聚合；未知不并入标准。</summary>
    public TelemetryQuery Query(string metric, string? model, DateTimeOffset start, DateTimeOffset end, int minutes, string? tier = null, string? origin = null, bool mismatchesOnly = false)
    {
        if (tier is not (null or "standard" or "fast" or "unknown")) throw new ArgumentOutOfRangeException(nameof(tier));
        if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (metric == RequestTelemetry.Metric) return QueryRequests(model, start, end, minutes, tier, origin, mismatchesOnly);
        using var db = Open();
        using var dimensions = db.CreateCommand(); dimensions.CommandText = "SELECT DISTINCT model FROM samples ORDER BY model LIMIT 256";
        var models = new List<string>(); using (var reader = dimensions.ExecuteReader()) while (reader.Read()) models.Add(reader.GetString(0));
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT min(end_ms),max(end_ms) FROM samples WHERE metric=$m AND ($model IS NULL OR model=$model) AND ($tier IS NULL OR tier=$tier)";
        cmd.Parameters.AddWithValue("$m", metric); cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tier", (object?)tier ?? DBNull.Value);
        DateTimeOffset? first = null, last = null;
        using (var reader = cmd.ExecuteReader()) if (reader.Read() && !reader.IsDBNull(0))
        { first = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)); last = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)); }
        cmd.Parameters.AddWithValue("$s", start.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$e", end.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$bucket", checked(minutes * 60000L));
        cmd.CommandText = "SELECT min(start_ms),max(end_ms),model,tier,sum(n),sum(total),max(res) FROM samples " +
            "WHERE metric=$m AND ($model IS NULL OR model=$model) AND ($tier IS NULL OR tier=$tier) AND end_ms >= $s AND end_ms <= $e " +
            "GROUP BY end_ms/$bucket,model,tier ORDER BY max(end_ms) DESC LIMIT 10001";
        var rows = new List<TelemetryBucket>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows.Add(new(
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            metric, reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetDouble(5), reader.GetInt32(6)));
        return new(rows.Take(10000).ToArray(), models.ToArray(), first, last, rows.Count > 10000);
    }

    /// <summary>后台按保留期降采样并回收页；空间接近上限时淘汰最早摘要，累计基线另保留以避免重复归因。</summary>
    public void Maintain(TelemetryOptions options, DateTimeOffset now)
    {
        if (!options.IsValid()) throw new InvalidDataException("Invalid telemetry retention settings");
        using var db = Open();
        using (var tx = db.BeginTransaction())
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM samples WHERE end_ms < $cut; DELETE FROM request_ttft WHERE end_ms < $cut; DELETE FROM baseline WHERE end_ns < $nano";
            cmd.Parameters.AddWithValue("$cut", now.AddDays(-options.RetentionDays).ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$nano", now.AddDays(-options.RetentionDays).ToUnixTimeMilliseconds() * 1000000L);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM diagnostics WHERE bucket_ms < $diag; DELETE FROM diagnostics WHERE rowid IN (SELECT rowid FROM diagnostics ORDER BY bucket_ms DESC LIMIT -1 OFFSET 50000)";
            cmd.Parameters.AddWithValue("$diag", now.AddDays(-7).ToUnixTimeMilliseconds()); cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT start_ms,end_ms,metric,model,tier,n,total,res,bounds,counts FROM samples " +
                "WHERE (res=0 AND end_ms<$detail) OR (res<3600 AND end_ms<$minute) ORDER BY end_ms LIMIT 2000";
            cmd.Parameters.AddWithValue("$detail", now.AddDays(-options.DetailDays).ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$minute", now.AddDays(-options.MinuteDays).ToUnixTimeMilliseconds());
            var old = new List<Row>();
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) old.Add(new(reader.GetInt64(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetDouble(6),
                reader.GetInt32(7), reader.GetString(8), JsonSerializer.Deserialize<long[]>(reader.GetString(9))!));
            foreach (var row in old)
            {
                using var del = db.CreateCommand(); del.Transaction = tx; Bind(del, row);
                del.CommandText = "DELETE FROM samples WHERE res=$r AND end_ms=$e AND metric=$m AND model=$model AND tier=$tier AND bounds=$b";
                del.ExecuteNonQuery();
                var seconds = row.End < now.AddDays(-options.MinuteDays).ToUnixTimeMilliseconds() ? 3600 : 60;
                var bucketTime = row.End / (seconds * 1000L) * seconds * 1000L;
                Merge(db, tx, row with { Start = Math.Min(row.Start, bucketTime), End = bucketTime, Res = seconds });
            }
            tx.Commit();
        }
        var budgetPages = options.MaximumMegabytes * 1024L * 1024 * 49 / 100 / 4096;
        _maximumPages = budgetPages;
        // 80% 开始回收，留出下一批写入空间。max_page_count 是最终硬边界，满盘错误仍向接收端暴露。
        while (Scalar(db, "PRAGMA page_count") > budgetPages * 0.8)
        {
            // 容量紧张先回收辅助诊断，不让新增诊断优先挤掉业务观测。
            if (Execute(db, "DELETE FROM diagnostics WHERE rowid IN (SELECT rowid FROM diagnostics ORDER BY bucket_ms LIMIT 1000)") > 0) continue;
            var removed = Execute(db, "DELETE FROM samples WHERE rowid IN (SELECT rowid FROM samples ORDER BY end_ms LIMIT 1000)");
            removed += Execute(db, "DELETE FROM request_ttft WHERE rowid IN (SELECT rowid FROM request_ttft ORDER BY end_ms LIMIT 1000)");
            if (removed <= 0) break;
            EvictedRows += removed;
        }
        if (Scalar(db, "PRAGMA page_count") > budgetPages) throw new IOException("Telemetry baseline exceeds storage budget");
        Execute(db, $"PRAGMA max_page_count={budgetPages}");
        using var meta = db.CreateCommand();
        meta.CommandText = "INSERT INTO metadata VALUES('evicted', $n) ON CONFLICT(key) DO UPDATE SET value=value+$n";
        meta.Parameters.AddWithValue("$n", EvictedRows); meta.ExecuteNonQuery(); EvictedRows = 0;
    }

    /// <summary>只读容量及淘汰计数，状态显示包含实际物理文件占用。</summary>
    public (long Bytes, long Evicted) Usage()
    {
        using var db = Open();
        return (new[] { _path, _path + "-journal", _path + "-wal", _path + "-shm" }.Where(File.Exists).Sum(p => new FileInfo(p).Length),
            Scalar(db, "SELECT coalesce((SELECT value FROM metadata WHERE key='evicted'),0)"));
    }

    /// <summary>执行内部固定语句；外部数值通过参数绑定，不吞掉 SQLite 原始错误。</summary>
    private static int Execute(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteNonQuery(); }
    /// <summary>读取内部单值统计供容量控制使用。</summary>
    private static long Scalar(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private sealed record Row(long Start, long End, string Metric, string Model, string Tier, long Count, double Sum, int Res, string Bounds, long[] Counts);
}
