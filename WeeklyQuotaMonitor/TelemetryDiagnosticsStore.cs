using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace WeeklyQuotaMonitor;

/// <summary>仅保存采集阶段和模型维度，不包含正文、资源属性或凭据；观测数仅用于已提交的增量。</summary>
public sealed record TelemetryDiagnosticEvent(string Pipeline, string Metric, string Model, string Outcome,
    DateTimeOffset? EventAt = null, long Observations = 0, string Reason = "");

public sealed record TelemetryDiagnosticSummary(string Model, long Received, long Stored, long Baselines,
    long Duplicates, long Discontinuities, long Rejected, long Ignored, long RequestEvents, long Empty, string Reason)
{
    public long WebSocketTimingPoints { get; init; }
    public long WebSocketEventPoints { get; init; }
}

public sealed partial class TelemetryStore
{
    /// <summary>追加独立诊断表，按接收时间五分钟聚合；既有额度、请求及直方图表不改写。</summary>
    private static void InitializeDiagnostics(SqliteConnection db) => Execute(db,
        "CREATE TABLE IF NOT EXISTS diagnostics(bucket_ms INTEGER NOT NULL,pipeline TEXT NOT NULL,metric TEXT NOT NULL,model TEXT NOT NULL,outcome TEXT NOT NULL,points INTEGER NOT NULL,observations INTEGER NOT NULL,first_ms INTEGER NOT NULL,last_ms INTEGER NOT NULL,event_ms INTEGER,reason TEXT NOT NULL,PRIMARY KEY(bucket_ms,pipeline,metric,model,outcome));");

    /// <summary>在已有数据事务内合并同一包的诊断维度，提交前不会宣称数据已落盘；原因仅保留最后一次。</summary>
    private static void WriteDiagnostics(SqliteConnection db, SqliteTransaction tx,
        IReadOnlyList<TelemetryDiagnosticEvent> events, DateTimeOffset receivedAt)
    {
        var now = receivedAt.ToUnixTimeMilliseconds();
        foreach (var group in events.GroupBy(e => (e.Pipeline, Metric: SafeDimension(e.Metric), Model: SafeDimension(e.Model), e.Outcome)))
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO diagnostics VALUES($b,$p,$m,$model,$o,$n,$obs,$t,$t,$event,$reason) " +
                "ON CONFLICT(bucket_ms,pipeline,metric,model,outcome) DO UPDATE SET points=points+$n,observations=observations+$obs,last_ms=$t,event_ms=max(coalesce(event_ms,0),coalesce($event,0)),reason=CASE WHEN $reason<>'' THEN $reason ELSE reason END";
            cmd.Parameters.AddWithValue("$b", now / 300000 * 300000);
            cmd.Parameters.AddWithValue("$p", group.Key.Pipeline); cmd.Parameters.AddWithValue("$m", group.Key.Metric);
            cmd.Parameters.AddWithValue("$model", group.Key.Model); cmd.Parameters.AddWithValue("$o", group.Key.Outcome);
            cmd.Parameters.AddWithValue("$n", group.LongCount()); cmd.Parameters.AddWithValue("$obs", group.Sum(e => e.Observations));
            cmd.Parameters.AddWithValue("$t", now);
            var lastEvent = group.Where(e => e.EventAt.HasValue).Select(e => e.EventAt!.Value.ToUnixTimeMilliseconds()).DefaultIfEmpty(0).Max();
            cmd.Parameters.AddWithValue("$event", lastEvent == 0 ? DBNull.Value : (object)lastEvent);
            var reason = group.LastOrDefault(e => e.Reason.Length > 0)?.Reason ?? "";
            cmd.Parameters.AddWithValue("$reason", reason.Length <= 512 ? reason : reason[..512]);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>拒绝包没有数据事务，单独保存最小诊断；数据库本身失败仍向调用方抛出原始错误。</summary>
    public void RecordDiagnostics(IReadOnlyList<TelemetryDiagnosticEvent> events, DateTimeOffset receivedAt)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        WriteDiagnostics(db, tx, events, receivedAt); tx.Commit();
    }

    /// <summary>指标维度只接受有限长度的标识符，防止错误包把任意文本保存为模型或指标名。</summary>
    private static string SafeDimension(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9_.-]{1,160}$") ? value : "unknown";

    /// <summary>按接收时间汇总当前指标，合并同窗请求日志模型，使“请求存在但该直方图未收到”能够显式显示。</summary>
    public IReadOnlyList<TelemetryDiagnosticSummary> QueryDiagnostics(string metric, string? model,
        DateTimeOffset start, DateTimeOffset end)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT model,outcome,points,observations,reason,metric FROM diagnostics " +
            "WHERE bucket_ms >= $s AND bucket_ms <= $e AND metric IN ($m,$r,'packet','transport.websocket_event','transport.websocket_timing') AND ($model IS NULL OR model=$model OR model='unknown') " +
            "ORDER BY last_ms,rowid";
        cmd.Parameters.AddWithValue("$s", start.ToUnixTimeMilliseconds() / 300000 * 300000);
        cmd.Parameters.AddWithValue("$e", end.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$m", metric);
        cmd.Parameters.AddWithValue("$r", RequestTelemetry.Metric); cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        var rows = new List<(string Model, string Outcome, long Points, long Observations, string Reason, string Metric)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5)));
        return rows.GroupBy(r => r.Model).Select(g =>
        {
            // 当前指标的处理点数与无法归属模型的整包错误分别计入，日志事件只用作覆盖对照。
            long Count(string outcome) => g.Where(r => r.Outcome == outcome && (r.Metric == metric || r.Metric == "packet")).Sum(r => r.Points);
            return new TelemetryDiagnosticSummary(g.Key, Count("received"), g.Where(r => r.Metric == metric && r.Outcome == "stored").Sum(r => r.Observations),
                Count("baseline"), Count("duplicate"), Count("discontinuity"), Count("rejected"), Count("ignored"),
                g.Where(r => r.Metric == RequestTelemetry.Metric && r.Outcome == "received").Sum(r => r.Points),
                Count("empty"),
                g.LastOrDefault(r => r.Reason.Length > 0 && (r.Metric == metric || r.Metric == "packet")).Reason ?? "")
            {
                WebSocketTimingPoints = g.Where(r => r.Metric == "transport.websocket_timing" && r.Outcome == "received").Sum(r => r.Points),
                WebSocketEventPoints = g.Where(r => r.Metric == "transport.websocket_event" && r.Outcome == "received").Sum(r => r.Points)
            };
        }).ToArray();
    }

    /// <summary>返回有限数量的全部指标诊断明细，包含未选择的新指标名和拒绝原因；时间窗按接收时间查询。</summary>
    public string DiagnosticReport(DateTimeOffset start, DateTimeOffset end)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT bucket_ms,last_ms,pipeline,metric,model,outcome,points,observations,event_ms,reason FROM diagnostics WHERE bucket_ms >= $s AND bucket_ms <= $e ORDER BY last_ms DESC LIMIT 2001";
        cmd.Parameters.AddWithValue("$s", start.ToUnixTimeMilliseconds() / 300000 * 300000);
        cmd.Parameters.AddWithValue("$e", end.ToUnixTimeMilliseconds());
        var lines = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (lines.Count == 2000) { lines.Add("... latest 2000 rows only"); break; }
            var bucket = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).LocalDateTime;
            var received = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).LocalDateTime;
            var eventTime = reader.IsDBNull(8) ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)).LocalDateTime.ToString("MM-dd HH:mm:ss");
            lines.Add($"{bucket:MM-dd HH:mm} | received {received:HH:mm:ss} | {reader.GetString(2)} | {reader.GetString(3)} | {reader.GetString(4)} | {reader.GetString(5)} | points={reader.GetInt64(6)} observations={reader.GetInt64(7)} | event={eventTime} | {reader.GetString(9)}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
