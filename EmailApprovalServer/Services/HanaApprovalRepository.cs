using Sap.Data.Hana;

namespace EmailApprovalServer.Services;

public sealed class HanaApprovalRepository
{
    private readonly ApprovalService _approvalService;

    public HanaApprovalRepository(ApprovalService approvalService) => _approvalService = approvalService;

    public async Task<IReadOnlyList<PendingApproval>> GetPendingAsync(bool onlyUnsent, CancellationToken cancellationToken)
    {
        var cfg = _approvalService.LoadConfig();
        await using var connection = new HanaConnection(BuildConnectionString(cfg));
        await connection.OpenAsync(cancellationToken);

        var unsentFilter = onlyUnsent ? "AND COALESCE(D.\"U_EMAILNOTI\", 'N') = 'N'" : string.Empty;
        var sql = $$"""
            SELECT W."WddCode", W."DraftEntry", A."USER_CODE" AS "ApproverId",
                   COALESCE(A."E_Mail", '') AS "ApproverEmail",
                   CASE W."ObjType"
                       WHEN '22' THEN '구매 오더'
                       WHEN '23' THEN '판매 견적'
                       WHEN '540000006' THEN '구매 견적'
                       ELSE W."ObjType"
                   END AS "DocumentType",
                   D."DocNum", C."USER_CODE" AS "Creator", D."CreateDate",
                   D."DocTotal", D."DocCur", COALESCE(D."Comments", '') AS "DraftRemarks"
              FROM "{{cfg.HanaSchema}}"."OWDD" W
              JOIN "{{cfg.HanaSchema}}"."WDD1" L
                ON L."WddCode" = W."WddCode" AND L."Status" = 'W'
              JOIN "{{cfg.HanaSchema}}"."OUSR" A ON A."USERID" = L."UserID"
              JOIN "{{cfg.HanaSchema}}"."ODRF" D ON D."DocEntry" = W."DraftEntry"
              LEFT JOIN "{{cfg.HanaSchema}}"."OUSR" C ON C."USERID" = D."UserSign"
             WHERE W."Status" = 'W'
               AND W."ObjType" IN ('22', '23', '540000006')
               {{unsentFilter}}
             ORDER BY W."WddCode", L."SortId"
            """;

        await using var command = new HanaCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PendingApproval>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PendingApproval(
                Convert.ToInt32(reader["WddCode"]),
                Convert.ToInt32(reader["DraftEntry"]),
                Convert.ToString(reader["ApproverId"]) ?? string.Empty,
                Convert.ToString(reader["DocumentType"]) ?? string.Empty,
                Convert.ToString(reader["DocNum"]) ?? string.Empty,
                Convert.ToString(reader["Creator"]) ?? string.Empty,
                Convert.ToDateTime(reader["CreateDate"]),
                Convert.ToDecimal(reader["DocTotal"]),
                Convert.ToString(reader["DocCur"]) ?? string.Empty,
                Convert.ToString(reader["DraftRemarks"]) ?? string.Empty,
                Convert.ToString(reader["ApproverEmail"]) ?? string.Empty));
        }
        return rows;
    }

    public async Task MarkDraftNotifiedAsync(int draftEntry, CancellationToken cancellationToken)
    {
        var cfg = _approvalService.LoadConfig();
        await using var connection = new HanaConnection(BuildConnectionString(cfg));
        await connection.OpenAsync(cancellationToken);

        // 승인 진행 중인 Drafts는 Service Layer PATCH 자체가 차단된다(234000125).
        // 알림 추적 전용 UDF 한 필드만 조건부로 갱신한다.
        var sql = $"""
            UPDATE "{cfg.HanaSchema}"."ODRF"
               SET "U_EMAILNOTI" = 'Y'
             WHERE "DocEntry" = ?
               AND "ObjType" IN ('22', '23', '540000006')
               AND COALESCE("U_EMAILNOTI", 'N') = 'N'
            """;
        await using var command = new HanaCommand(sql, connection);
        command.Parameters.Add(new HanaParameter { Value = draftEntry });
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected is not 0 and not 1)
        {
            throw new InvalidOperationException(
                $"Unexpected ODRF update count for DraftEntry {draftEntry}: {affected}.");
        }
    }

    public async Task<DraftDetail> GetDraftDetailAsync(int draftEntry, CancellationToken cancellationToken)
    {
        var cfg = _approvalService.LoadConfig();
        await using var connection = new HanaConnection(BuildConnectionString(cfg));
        await connection.OpenAsync(cancellationToken);

        var headerSql = $"SELECT \"CardCode\", \"CardName\", \"DocNum\", \"DocTotal\", \"DocCur\" FROM \"{cfg.HanaSchema}\".\"ODRF\" WHERE \"DocEntry\"=? AND \"ObjType\" IN ('22','23','540000006')";
        await using var headerCommand = new HanaCommand(headerSql, connection);
        headerCommand.Parameters.Add(new HanaParameter { Value = draftEntry });
        await using var headerReader = await headerCommand.ExecuteReaderAsync(cancellationToken);
        if (!await headerReader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"DraftEntry {draftEntry} was not found.");
        }
        var detail = new DraftDetail(
            Convert.ToString(headerReader["CardCode"]) ?? string.Empty,
            Convert.ToString(headerReader["CardName"]) ?? string.Empty,
            Convert.ToString(headerReader["DocNum"]) ?? string.Empty,
            Convert.ToDecimal(headerReader["DocTotal"]),
            Convert.ToString(headerReader["DocCur"]) ?? string.Empty,
            new List<DraftLine>());
        await headerReader.CloseAsync();

        // 구매 견적의 품목 번호는 표준 ItemCode가 아니라 화면 UDF인 U_ITEMCODE를 사용한다.
        // 다른 전표 유형은 기존 표준 품목 코드를 그대로 유지한다.
        var lineSql = $"""
            SELECT L."LineNum",
                   CASE WHEN D."ObjType" = '540000006'
                        THEN COALESCE(L."U_ITEMCODE", '')
                        ELSE COALESCE(L."ItemCode", '')
                   END AS "ItemCode",
                   L."Quantity", L."Price"
              FROM "{cfg.HanaSchema}"."DRF1" L
              JOIN "{cfg.HanaSchema}"."ODRF" D ON D."DocEntry" = L."DocEntry"
             WHERE L."DocEntry" = ?
             ORDER BY L."LineNum"
            """;
        await using var lineCommand = new HanaCommand(lineSql, connection);
        lineCommand.Parameters.Add(new HanaParameter { Value = draftEntry });
        await using var lineReader = await lineCommand.ExecuteReaderAsync(cancellationToken);
        while (await lineReader.ReadAsync(cancellationToken))
        {
            detail.Lines.Add(new DraftLine(
                Convert.ToString(lineReader["ItemCode"]) ?? string.Empty,
                Convert.ToDecimal(lineReader["Quantity"]),
                Convert.ToDecimal(lineReader["Price"])));
        }
        return detail;
    }

    private static string BuildConnectionString(ConfigData cfg) =>
        $"Server={cfg.HanaHost}:{cfg.HanaPort};UserID={cfg.HanaUser};Password={cfg.HanaPassword};Current Schema={cfg.HanaSchema}";
}

public sealed record PendingApproval(
    int RequestId,
    int DraftEntry,
    string ApproverId,
    string DocumentType,
    string DocumentNumber,
    string Creator,
    DateTime CreatedDate,
    decimal DocumentTotal,
    string Currency,
    string DraftRemarks,
    string ApproverEmail = "")
{
    public ApprovalNotificationRequest ToNotification() => new(
        RequestId.ToString(), ApproverId, DocumentType, DocumentNumber, DraftEntry.ToString(), Creator,
        CreatedDate.ToString("yyyy.MM.dd"), $"{DocumentTotal:N0} {Currency}", DraftRemarks, ApproverEmail);
}

public sealed record DraftDetail(
    string SupplierCode,
    string SupplierName,
    string DocumentNumber,
    decimal DocumentTotal,
    string Currency,
    List<DraftLine> Lines);

public sealed record DraftLine(string ItemCode, decimal Quantity, decimal UnitPrice);
