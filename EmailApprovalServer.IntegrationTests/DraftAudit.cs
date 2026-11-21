using System.Globalization;
using System.Xml.Linq;
using Sap.Data.Hana;

internal static class DraftAudit
{
    public static async Task Run(string configPath, int draftEntry)
    {
        if (draftEntry <= 0) throw new ArgumentOutOfRangeException(nameof(draftEntry));
        var root = XDocument.Load(configPath).Root!;
        var hana = root.Element("Hana")!;
        string V(string name) => hana.Element(name)!.Value;
        await using var connection = new HanaConnection($"Server={V("Host")}:{V("Port")};UserID={V("User")};Password={V("Password")};Current Schema={V("Schema")}");
        await connection.OpenAsync();
        Console.WriteLine("READ ONLY company=" + V("Schema") + ", DraftEntry=" + draftEntry);
        async Task Query(string label, string sql, params object[] values)
        {
            Console.WriteLine(label);
            await using var command = new HanaCommand(sql, connection);
            foreach (var value in values) command.Parameters.Add(new HanaParameter { Value = value });
            await using var reader = await command.ExecuteReaderAsync();
            int count = 0;
            while (await reader.ReadAsync())
            {
                count++;
                Console.WriteLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i) + "=" + Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            }
            Console.WriteLine("Rows=" + count);
        }
        await Query("Draft", """
            SELECT "DocEntry","DocNum","ObjType","DocStatus","WddStatus","CardCode","DocTotal","DocCur","CreateDate","UpdateDate","UpdateTS"
              FROM "ODRF" WHERE "DocEntry"=?
            """, draftEntry);
        await Query("Approvals", """
            SELECT "WddCode","DraftEntry","DocEntry","ObjType","IsDraft","Status","CurrStep","CreateDate","CreateTime","Remarks"
              FROM "OWDD" WHERE "DraftEntry"=? ORDER BY "WddCode"
            """, draftEntry);
        await Query("Approval steps", """
            SELECT S."WddCode",S."SortId",S."StepCode",U."USER_CODE",S."Status",S."UpdateDate",S."UpdateTime",S."AuthUpdDat",S."AuthUpdTim",S."Remarks"
              FROM "WDD1" S JOIN "OWDD" W ON W."WddCode"=S."WddCode"
              LEFT JOIN "OUSR" U ON U."USERID"=S."UserID"
             WHERE W."DraftEntry"=? ORDER BY S."WddCode",S."SortId",S."UserID"
            """, draftEntry);
        await Query("Purchase orders matching draftKey or OWDD final key", """
            SELECT "DocEntry","DocNum","draftKey","DocStatus","CANCELED","CardCode","DocTotal","DocCur","CreateDate","CreateTS"
              FROM "OPOR" WHERE "draftKey"=? OR "DocEntry" IN
                (SELECT "DocEntry" FROM "OWDD" WHERE "DraftEntry"=? AND "ObjType"='22' AND "IsDraft"='N')
            """, draftEntry, draftEntry);
        await Query("Preserved current approvals", """
            SELECT "Code","U_Status","U_Draft","U_DocEntry","U_DocNum","U_DocState","U_Amount","U_Source","U_Captured","U_Hash"
              FROM "@PVI_ARVC" WHERE "U_Draft"=? ORDER BY "Code"
            """, draftEntry.ToString(CultureInfo.InvariantCulture));
        await Query("Preserved history", """
            SELECT "Code","U_WddCode","U_Status","U_DocEntry","U_DocNum","U_Source","U_Captured"
              FROM "@PVI_APVH" WHERE "U_Draft"=? ORDER BY "U_Captured"
            """, draftEntry.ToString(CultureInfo.InvariantCulture));
        await Query("Report filters and display keys", """
            SELECT "U_WddCode", "U_User", "U_CreatorName", "U_DraftNum", "U_Draft", "U_DocEntry", "U_Status", "U_ObjType",
                   CASE WHEN "U_Payload" LIKE '%<Login>dir03</Login>%' THEN 'Y' ELSE 'N' END AS "MatchesApproverDir03",
                   CASE WHEN "U_User"='dir03' THEN 'Y' ELSE 'N' END AS "MatchesRequesterDir03"
              FROM "@PVI_ARVC" WHERE "U_Draft"=?
            """, draftEntry.ToString(CultureInfo.InvariantCulture));
    }
}
