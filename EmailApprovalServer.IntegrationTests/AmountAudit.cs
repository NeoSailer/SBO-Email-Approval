using System.Xml.Linq;
using Sap.Data.Hana;

internal static class AmountAudit
{
    // 진단 전용: 지정된 회사에서 SELECT만 실행한다. 설정/승인/문서를 변경하지 않는다.
    public static async Task Run(string configPath, string? reportSqlPath = null)
    {
        var hana = XDocument.Load(configPath).Root!.Element("Hana")!;
        string V(string name) => hana.Element(name)!.Value;
        await using var connection = new HanaConnection($"Server={V("Host")}:{V("Port")};UserID={V("User")};Password={V("Password")};Current Schema={V("Schema")}");
        await connection.OpenAsync();
        if (reportSqlPath is not null)
        {
            await using var report = new HanaCommand(File.ReadAllText(reportSqlPath), connection);
            await using var data = await report.ExecuteReaderAsync();
            int count = 0, nonzero = 0;
            while (await data.ReadAsync())
            {
                count++;
                if (Convert.ToDecimal(data["Amount"]) != 0m) nonzero++;
            }
            Console.WriteLine($"Read-only report SQL: {V("Schema")}, rows={count}, nonzero amounts={nonzero}");
            if (count == 0 || count != nonzero) throw new Exception("Expected all current audit records to have nonzero source totals.");
            return;
        }
        await using (var probe = new HanaCommand("SELECT \"DocTotal\" FROM \"ODRF\" WHERE \"DocTotal\">0 LIMIT 1", connection))
        await using (var data = await probe.ExecuteReaderAsync())
        {
            if (await data.ReadAsync())
            {
                var raw = data.GetValue(0);
                Console.WriteLine($"Source value type={raw.GetType().FullName}, raw={raw}, Double={Convert.ToDouble(raw)}, Decimal={Convert.ToDecimal(raw)}, GetDecimal={data.GetDecimal(0)}");
            }
        }
        foreach (var amount in new object[] { 12345.67d, 12345.67m })
        {
            await using var probe = new HanaCommand("SELECT CAST(? AS DECIMAL(19,6)) FROM DUMMY", connection);
            probe.Parameters.Add(new HanaParameter { Value = amount });
            Console.WriteLine($"Parameter {amount.GetType().Name}: {await probe.ExecuteScalarAsync()}");
        }
        var sql = """
            SELECT C."Code", C."U_ObjType", C."U_Draft", C."U_DocEntry", C."U_Amount", C."U_AmtKnown",
                   D."DocTotal" AS "DraftTotal", F."DocTotal" AS "FinalTotal"
              FROM "@PVI_ARVC" C
              LEFT JOIN "ODRF" D ON TO_NVARCHAR(D."DocEntry")=C."U_Draft" AND D."ObjType"=C."U_ObjType"
              LEFT JOIN (SELECT '22' AS "Type", "DocEntry", "DocTotal" FROM "OPOR"
                         UNION ALL SELECT '23', "DocEntry", "DocTotal" FROM "OQUT"
                         UNION ALL SELECT '540000006', "DocEntry", "DocTotal" FROM "OPQT") F
                ON TO_NVARCHAR(F."DocEntry")=C."U_DocEntry" AND F."Type"=C."U_ObjType"
             WHERE C."U_ObjType" IN ('22','23','540000006') ORDER BY C."Code"
            """;
        await using var command = new HanaCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Console.WriteLine("Read-only amount audit: " + V("Schema"));
        while (await reader.ReadAsync())
            Console.WriteLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i) + "=" + Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
    }
}
