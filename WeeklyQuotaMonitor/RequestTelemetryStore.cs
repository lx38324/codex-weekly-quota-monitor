using Microsoft.Data.Sqlite;

namespace WeeklyQuotaMonitor;

public sealed partial class TelemetryStore
{
    /// <summary>追加请求摘要表，不改写既有直方图；原始事件精度参与自然去重键。</summary>
    private static void InitializeRequests(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText =
        "CREATE TABLE IF NOT EXISTS request_ttft(conversation TEXT NOT NULL,ticks INTEGER NOT NULL,model TEXT NOT NULL,end_ms INTEGER NOT NULL,ttft REAL NOT NULL,reported TEXT,output INTEGER,reasoning INTEGER,tier TEXT NOT NULL,source TEXT NOT NULL,PRIMARY KEY(conversation,ticks,model));" +
        "CREATE INDEX IF NOT EXISTS request_time ON request_ttft(end_ms);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA table_info(request_ttft)";
        var columns = new HashSet<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        foreach (var (name, value) in new[] { ("origin", "unknown"), ("local_model", ""), ("association", "unmatched"), ("match_basis", "none") })
            if (!columns.Contains(name))
            {
                cmd.CommandText = $"ALTER TABLE request_ttft ADD COLUMN {name} TEXT NOT NULL DEFAULT '{value}'";
                cmd.ExecuteNonQuery();
            }
        cmd.CommandText = "PRAGMA user_version=2"; cmd.ExecuteNonQuery(); tx.Commit();
    }

    /// <summary>以自然键去重并验证重传内容；事件直接层级可补强旧关联，冲突数据整包回滚。</summary>
    public void AppendRequests(IReadOnlyList<RequestLatency> requests, RequestModeIndex index, DateTimeOffset? receivedAt = null)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var diagnostics = new List<TelemetryDiagnosticEvent>();
        foreach (var request in requests)
        {
            var r = request;
            var duplicate = false;
            (string Tier, string Source)? retainedMode = null;
            RequestAssociation? retainedAssociation = null;
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$c", r.Conversation); cmd.Parameters.AddWithValue("$t", r.Time.UtcTicks);
            cmd.Parameters.AddWithValue("$m", r.Model);
            cmd.CommandText = "SELECT ttft,reported,output,reasoning,tier,source,origin,local_model,association,match_basis FROM request_ttft WHERE conversation=$c AND ticks=$t AND model=$m";
            using (var reader = cmd.ExecuteReader()) if (reader.Read())
            {
                duplicate = true;
                var reported = reader.IsDBNull(1) ? null : reader.GetString(1);
                long? output = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                long? reasoning = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                if (reader.GetDouble(0) != r.TtftMs || output != r.OutputTokens || reasoning != r.ReasoningTokens ||
                    reported is not null && r.ReportedTier is not null && reported != r.ReportedTier)
                    throw new InvalidDataException("Conflicting duplicate request telemetry");
                r = r with { ReportedTier = reported ?? r.ReportedTier };
                var origin = reader.GetString(6);
                if (origin != "unknown" && r.Origin != "unknown" && origin != r.Origin)
                    throw new InvalidDataException("Conflicting duplicate request origin");
                r = r with { Origin = origin != "unknown" ? origin : r.Origin };
                if ((index.First is null || r.Time < index.First) && r.Origin == "unknown")
                    retainedAssociation = new(reader.GetString(7), reader.GetString(8), reader.GetString(9));
                if (r.ReportedTier is null && (index.First is null || r.Time < index.First))
                    retainedMode = (reader.GetString(4), reader.GetString(5));
            }
            var mode = r.Origin == "unknown" ? retainedMode ?? index.Resolve(r) : index.Resolve(r);
            var association = retainedAssociation ?? index.Associate(r);
            cmd.CommandText = "INSERT INTO request_ttft(conversation,ticks,model,end_ms,ttft,reported,output,reasoning,tier,source,origin,local_model,association,match_basis) VALUES($c,$t,$m,$e,$v,$p,$o,$r,$tier,$source,$origin,$local,$association,$basis) " +
                "ON CONFLICT(conversation,ticks,model) DO UPDATE SET reported=$p,tier=$tier,source=$source,origin=$origin,local_model=$local,association=$association,match_basis=$basis";
            cmd.Parameters.AddWithValue("$e", r.Time.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$v", r.TtftMs);
            cmd.Parameters.AddWithValue("$p", (object?)r.ReportedTier ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$o", (object?)r.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", (object?)r.ReasoningTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tier", mode.Tier); cmd.Parameters.AddWithValue("$source", mode.Source);
            cmd.Parameters.AddWithValue("$origin", r.Origin); cmd.Parameters.AddWithValue("$local", association.LocalModel);
            cmd.Parameters.AddWithValue("$association", association.State); cmd.Parameters.AddWithValue("$basis", association.Basis);
            cmd.ExecuteNonQuery();
            diagnostics.Add(new("logs", RequestTelemetry.Metric, r.Model, "received", r.Time));
            diagnostics.Add(new("logs", RequestTelemetry.Metric, r.Model, duplicate ? "duplicate" : "stored", r.Time, duplicate ? 0 : 1));
        }
        WriteDiagnostics(db, tx, diagnostics, receivedAt ?? DateTimeOffset.UtcNow);
        tx.Commit();
    }

    /// <summary>本地日志晚到时重新关联覆盖区间内的非直报请求；过期样本不抹除历史已确认关联。</summary>
    public void ReconcileRequests(RequestModeIndex index)
    {
        if (index.First is null) return;
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT conversation,ticks,model,ttft,output,reasoning,reported,origin FROM request_ttft WHERE end_ms >= $first";
        cmd.Parameters.AddWithValue("$first", index.First.Value.ToUnixTimeMilliseconds());
        var rows = new List<RequestLatency>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
            rows.Add(new(reader.GetString(0), new(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), reader.GetDouble(3), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(7)));
        foreach (var r in rows)
        {
            var mode = index.Resolve(r);
            var association = index.Associate(r);
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = "UPDATE request_ttft SET tier=$tier,source=$source,local_model=$local,association=$association,match_basis=$basis WHERE conversation=$c AND ticks=$t AND model=$m AND (tier<>$tier OR source<>$source OR local_model<>$local OR association<>$association OR match_basis<>$basis)";
            update.Parameters.AddWithValue("$local", association.LocalModel); update.Parameters.AddWithValue("$association", association.State);
            update.Parameters.AddWithValue("$basis", association.Basis);
            update.Parameters.AddWithValue("$tier", mode.Tier); update.Parameters.AddWithValue("$source", mode.Source);
            update.Parameters.AddWithValue("$c", r.Conversation); update.Parameters.AddWithValue("$t", r.Time.UtcTicks);
            update.Parameters.AddWithValue("$m", r.Model); update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>请求 TTFT 独立查询，按模型和模式聚合；同桶来源混合明确标注，不重复绘制同模式的点。</summary>
    private TelemetryQuery QueryRequests(string? model, DateTimeOffset start, DateTimeOffset end, int minutes, string? tier, string? origin, bool mismatchesOnly)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$origin", (object?)origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mismatch", mismatchesOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$tier", (object?)tier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
        const string scope = " FROM request_ttft WHERE ($tier IS NULL OR tier=$tier) AND ($origin IS NULL OR origin=$origin) AND ($mismatch=0 OR association='model-mismatch')";
        cmd.CommandText = "SELECT DISTINCT model" + scope + " AND end_ms >= $start AND end_ms <= $end ORDER BY model LIMIT 256";
        var models = new List<string>(); using (var reader = cmd.ExecuteReader()) while (reader.Read()) models.Add(reader.GetString(0));
        const string filter = scope + " AND ($model IS NULL OR model=$model)";
        cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        cmd.CommandText = "SELECT min(end_ms),max(end_ms)" + filter;
        DateTimeOffset? first = null, last = null;
        using (var reader = cmd.ExecuteReader()) if (reader.Read() && !reader.IsDBNull(0))
        { first = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)); last = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)); }
        cmd.Parameters.AddWithValue("$bucket", minutes * 60000L);
        cmd.CommandText = "SELECT min(end_ms),max(end_ms),model,tier,count(*),sum(ttft),CASE WHEN count(DISTINCT source)=1 THEN min(source) ELSE 'mixed' END," +
            "CASE WHEN count(DISTINCT local_model)=1 THEN min(local_model) ELSE 'mixed' END," +
            "CASE WHEN count(DISTINCT origin)=1 THEN min(origin) ELSE 'mixed' END," +
            "CASE WHEN count(DISTINCT association)=1 THEN min(association) ELSE 'mixed' END," +
            "CASE WHEN count(DISTINCT match_basis)=1 THEN min(match_basis) ELSE 'mixed' END," +
            "sum(CASE WHEN association='model-mismatch' THEN 1 ELSE 0 END)" + filter +
            " AND end_ms >= $start AND end_ms <= $end GROUP BY end_ms/$bucket,model,tier ORDER BY max(end_ms) DESC LIMIT 10001";
        var rows = new List<TelemetryBucket>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows.Add(new(
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            RequestTelemetry.Metric, reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetDouble(5), 0, reader.GetString(6),
            reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetInt64(11)));
        return new(rows.Take(10000).ToArray(), models.ToArray(), first, last, rows.Count > 10000);
    }
}
