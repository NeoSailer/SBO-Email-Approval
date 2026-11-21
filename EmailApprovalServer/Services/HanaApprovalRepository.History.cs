using System.Data;
using System.Globalization;
using Sap.Data.Hana;

namespace EmailApprovalServer.Services;

public sealed partial class HanaApprovalRepository
{
    // ApprovalService도 저장소를 생성하므로 모든 인스턴스가 같은 동기화 잠금을 사용한다.
    private static readonly SemaphoreSlim HistoryGate = new(1, 1);

    public Task PreserveApprovalHistoryAsync(int draftEntry, CancellationToken cancellationToken)
    {
        if (draftEntry <= 0) throw new ArgumentOutOfRangeException(nameof(draftEntry));
        return SynchronizeHistoryAsync(null, draftEntry, true, cancellationToken);
    }

    public Task SynchronizeApprovalHistoryAsync(int? requestId, CancellationToken cancellationToken)
    {
        if (requestId is <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        return SynchronizeHistoryAsync(requestId, null, false, cancellationToken);
    }

    public Task SynchronizeDraftHistoryAsync(int draftEntry, CancellationToken cancellationToken) =>
        draftEntry > 0 ? SynchronizeHistoryAsync(null, draftEntry, false, cancellationToken)
            : throw new ArgumentOutOfRangeException(nameof(draftEntry));

    private async Task SynchronizeHistoryAsync(int? requestId, int? draftEntry, bool requireApproved, CancellationToken cancellationToken)
    {
        await HistoryGate.WaitAsync(cancellationToken);
        try
        {
            await SynchronizeHistoryCoreAsync(requestId, draftEntry, requireApproved, cancellationToken);
        }
        finally { HistoryGate.Release(); }
    }

    private async Task SynchronizeHistoryCoreAsync(int? requestId, int? draftEntry, bool requireApproved, CancellationToken cancellationToken)
    {
        var cfg = _approvalService.LoadConfig();
        await using var connection = new HanaConnection(BuildConnectionString(cfg));
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var schema = "\"" + cfg.HanaSchema.Replace("\"", "\"\"") + "\".";

        async Task<List<Dictionary<string, object>>> Read(string sql, params object[] parameters)
        {
            await using var command = new HanaCommand(sql, connection) { Transaction = transaction };
            foreach (var value in parameters) command.Parameters.Add(new HanaParameter { Value = value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<Dictionary<string, object>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i);
                    // HanaDecimal은 reader의 네이티브 버퍼를 참조하므로 닫기 전에 CLR decimal로 복사한다.
                    row[reader.GetName(i)] = value is HanaDecimal ? reader.GetDecimal(i) : value;
                }
                rows.Add(row);
            }
            return rows;
        }

        async Task Write(string table, string key, Dictionary<string, object> values, bool update)
        {
            var fields = values.Keys.ToArray();
            var sql = update
                ? $"UPDATE {schema}\"{table}\" SET " + string.Join(",", fields.Select(f => $"\"U_{f}\" = ?")) + " WHERE \"Code\" = ?"
                : $"INSERT INTO {schema}\"{table}\" (\"Code\",\"Name\"," + string.Join(",", fields.Select(f => $"\"U_{f}\"")) + ") VALUES (?, ?," + string.Join(",", fields.Select(_ => "?")) + ")";
            await using var command = new HanaCommand(sql, connection) { Transaction = transaction };
            if (!update)
            {
                command.Parameters.Add(new HanaParameter { Value = key });
                command.Parameters.Add(new HanaParameter { Value = key });
            }
            foreach (var field in fields)
                command.Parameters.Add(field == "Amount"
                    ? new HanaParameter { HanaDbType = HanaDbType.Decimal, Precision = 19, Scale = 6, Value = Convert.ToDecimal(values[field], CultureInfo.InvariantCulture) }
                    : new HanaParameter { Value = values[field] });
            if (update) command.Parameters.Add(new HanaParameter { Value = key });
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"Approval history write failed: {table}, WddCode {values["WddCode"]}.");
        }

        // 요청 하나만 저장하면 동일 초안의 다른 승인 템플릿/재요청 이력이 유실될 수 있다.
        var headerFilter = requestId.HasValue ? " AND W.\"WddCode\" = ?" : draftEntry.HasValue ? " AND W.\"DraftEntry\" = ?" : "";
        var parameters = requestId.HasValue ? new object[] { requestId.Value } : draftEntry.HasValue ? new object[] { draftEntry.Value } : Array.Empty<object>();
        var headers = await Read($"""
            SELECT W.*, U."USER_CODE" AS "Login", U."U_NAME" AS "LoginName", T."Name" AS "TemplateName"
              FROM {schema}"OWDD" W
              LEFT JOIN {schema}"OUSR" U ON W."OwnerID" = U."USERID"
              LEFT JOIN {schema}"OWTM" T ON W."WtmCode" = T."WtmCode"
             WHERE W."ObjType" IN ('22','23','540000006') {headerFilter} ORDER BY W."WddCode"
            """, parameters);
        if (requireApproved && (!headers.Any(h => ApprovalHistorySnapshot.Text(h, "Status") == "Y" && ApprovalHistorySnapshot.Text(h, "IsDraft") == "Y") ||
            headers.Any(h => ApprovalHistorySnapshot.Text(h, "Status") == "W" && ApprovalHistorySnapshot.Text(h, "IsDraft") == "Y")))
            throw new InvalidOperationException($"DraftEntry {draftEntry} is not fully approved; conversion was stopped.");

        var currentFilter = requestId.HasValue ? " AND \"Code\" = ?" : draftEntry.HasValue ? " AND \"U_Draft\" = ?" : "";
        var currentParameters = parameters.Select(p => (object)Convert.ToString(p, CultureInfo.InvariantCulture)!).ToArray();
        var currents = (await Read($"SELECT * FROM {schema}\"@PVI_ARVC\" WHERE \"U_ObjType\" IN ('22','23','540000006'){currentFilter} ORDER BY \"Code\" FOR UPDATE", currentParameters))
            .ToDictionary(r => ApprovalHistorySnapshot.Text(r, "Code"));
        var byKey = headers.ToDictionary(r => ApprovalHistorySnapshot.Text(r, "WddCode"));
        foreach (var key in byKey.Keys.Union(currents.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            byKey.TryGetValue(key, out var header);
            currents.TryGetValue(key, out var current);
            var type = header is null ? ApprovalHistorySnapshot.Text(current!, "U_ObjType") : ApprovalHistorySnapshot.Text(header, "ObjType");
            var draftKey = header is null ? ApprovalHistorySnapshot.Text(current!, "U_Draft") : ApprovalHistorySnapshot.Draft(header, current!);
            var drafts = await Read($"""
            SELECT D.*, U."U_NAME" AS "DraftUserName", U."USER_CODE" AS "DraftLogin"
              FROM {schema}"ODRF" D LEFT JOIN {schema}"OUSR" U ON D."UserSign2" = U."USERID"
             WHERE D."DocEntry" = ? AND D."ObjType" = ?
            """, ApprovalHistorySnapshot.Key(draftKey), type);
            if (requireApproved && drafts.Count != 1) throw new InvalidOperationException($"DraftEntry {draftEntry} could not be preserved; conversion was stopped.");

            var table = type switch { "22" => "OPOR", "23" => "OQUT", "540000006" => "OPQT", _ => throw new InvalidOperationException("Unsupported document type.") };
            var finalEntry = header is not null && ApprovalHistorySnapshot.Text(header, "IsDraft") == "N"
                ? ApprovalHistorySnapshot.Text(header, "DocEntry") : ApprovalHistorySnapshot.Text(current!, "U_DocEntry");
            var documents = await Read($"SELECT \"DocEntry\",\"DocNum\",\"DocDate\",\"DocTotal\",\"DocCur\",\"DocStatus\",\"CANCELED\" FROM {schema}\"{table}\" WHERE \"DocEntry\" = ?", ApprovalHistorySnapshot.Key(finalEntry));
            if (documents.Count == 0 && ApprovalHistorySnapshot.Key(draftKey) > 0)
                documents = await Read($"SELECT \"DocEntry\",\"DocNum\",\"DocDate\",\"DocTotal\",\"DocCur\",\"DocStatus\",\"CANCELED\" FROM {schema}\"{table}\" WHERE \"draftKey\" = ?", ApprovalHistorySnapshot.Key(draftKey));
            if (documents.Count > 1) throw new InvalidOperationException($"Ambiguous official document for draft {draftKey}; history was not overwritten.");
            var document = documents.SingleOrDefault();
            var steps = new List<Dictionary<string, object>>();
            if (header is not null)
            {
                steps = await Read($"""
                SELECT S.*, U."USER_CODE" AS "Login", U."U_NAME" AS "ApproverName", T."Name" AS "StageName"
                  FROM {schema}"WDD1" S
                  LEFT JOIN {schema}"OUSR" U ON S."UserID" = U."USERID"
                  LEFT JOIN {schema}"OWST" T ON S."StepCode" = T."WstCode"
                 WHERE S."WddCode" = ? ORDER BY S."SortId", S."UserID"
                """, int.Parse(key, CultureInfo.InvariantCulture));
                if (requireApproved && steps.Count == 0) throw new InvalidOperationException($"WddCode {key} has no approval steps; conversion was stopped.");
            }
            var values = ApprovalHistorySnapshot.Reconcile(header!, steps, current!, document!, drafts.SingleOrDefault()!);
            var content = ApprovalHistorySnapshot.Fingerprint(values);
            // 최신 상태와 불변 이력이 모두 존재해야 백업 완료로 인정한다.
            var unchanged = current is not null && ApprovalHistorySnapshot.Text(current, "U_Content") == content &&
                ApprovalHistorySnapshot.StoredValuesMatch(values, current);
            var hash = unchanged ? ApprovalHistorySnapshot.Text(current!, "U_Hash")
                : ApprovalHistorySnapshot.EventKey(ApprovalHistorySnapshot.Text(current!, "U_Hash"), content);
            var existing = await Read($"SELECT \"U_Captured\" FROM {schema}\"@PVI_APVH\" WHERE \"Code\" = ?", hash);
            values["Content"] = content;
            values["Hash"] = hash;
            values["Captured"] = existing.Count > 0 ? existing[0]["U_Captured"] : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            if (existing.Count == 0) await Write("@PVI_APVH", hash, values, false);
            if (!unchanged || existing.Count == 0) await Write("@PVI_ARVC", key, values, current is not null);
        }
        transaction.Commit();
    }
}
