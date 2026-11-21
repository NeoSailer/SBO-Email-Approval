using System.Xml.Linq;
using EmailApprovalServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sap.Data.Hana;

if (args.Length == 3 && args[0] == "--inspect-draft")
{
    await DraftAudit.Run(args[1], int.Parse(args[2]));
    return;
}

if (args.Length == 3 && args[0] == "--audit-report")
{
    await AmountAudit.Run(args[1], args[2]);
    return;
}

if (args.Length == 2 && args[0] == "--audit-amounts")
{
    await AmountAudit.Run(args[1]);
    return;
}

if (args.Length < 2 || args[0] != "--test-SBOTSS")
    throw new ArgumentException("Usage: --test-SBOTSS <source-config> [preserve|convert <draftEntry>]");
var xml = XDocument.Load(Path.GetFullPath(args[1]));
xml.Root!.Element("Sap")!.Element("CompanyDb")!.Value = "SBOTSS";
xml.Root!.Element("Hana")!.Element("Schema")!.Value = "SBOTSS";
Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Config"));
xml.Save(Path.Combine(AppContext.BaseDirectory, "Config", "appsettings.xml"));
var service = new ApprovalService(new ConfigurationBuilder().Build(), NullLogger<ApprovalService>.Instance);
var cfg = service.LoadConfig();
if (cfg.CompanyDb != "SBOTSS" || cfg.HanaSchema != "SBOTSS") throw new Exception("Test company guard failed.");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await using var connection = new HanaConnection($"Server={cfg.HanaHost}:{cfg.HanaPort};UserID={cfg.HanaUser};Password={cfg.HanaPassword};Current Schema=SBOTSS");
await connection.OpenAsync(timeout.Token);
Console.WriteLine("PASS HANA connection: SBOTSS");
async Task<List<Dictionary<string, object>>> Read(string sql, params object[] values)
{
    await using var command = new HanaCommand(sql, connection);
    foreach (var value in values) command.Parameters.Add(new HanaParameter { Value = value });
    await using var reader = await command.ExecuteReaderAsync(timeout.Token);
    var rows = new List<Dictionary<string, object>>();
    while (await reader.ReadAsync(timeout.Token))
    {
        var row = new Dictionary<string, object>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.IsDBNull(i) ? "" : reader.GetValue(i);
            row[reader.GetName(i)] = value is HanaDecimal ? reader.GetDecimal(i) : value;
        }
        rows.Add(row);
    }
    return rows;
}
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
var fields = await Read("SELECT \"TableID\", COUNT(*) AS \"N\" FROM \"CUFD\" WHERE \"TableID\" IN ('@PVI_APVH','@PVI_ARVC') GROUP BY \"TableID\"");
Check(fields.Count == 2 && fields.All(r => Convert.ToInt32(r["N"]) == 28), "two UDTs with 28 fields each");
var repository = new HanaApprovalRepository(service);
var approved = await repository.GetApprovedDraftsAsync(null, timeout.Token);
Console.WriteLine("Approved drafts: " + string.Join(", ", approved.Select(d => $"{d.DraftEntry}/WddCode={d.RequestId}/Type={d.ObjectType}")));
if (args.Length == 2 || args[2] == "convert")
{
    await service.ExecuteSapRequestAsync(HttpMethod.Get, "Drafts?$top=1&$select=DocEntry", null, cfg.UserName, cfg.Password);
    Console.WriteLine("PASS Service Layer login and Drafts GET: SBOTSS");
}
if (args.Length == 2) return;
if (args.Length == 3 && args[2] == "sync")
{
    var priorRows = await Read("SELECT * FROM \"@PVI_ARVC\" WHERE \"U_ObjType\" IN ('22','23','540000006')");
    await repository.SynchronizeApprovalHistoryAsync(null, timeout.Token);
    var currentRows = await Read("SELECT * FROM \"@PVI_ARVC\" WHERE \"U_ObjType\" IN ('22','23','540000006')");
    var sourceHeaders = await Read("SELECT \"WddCode\",\"Status\" FROM \"OWDD\" WHERE \"ObjType\" IN ('22','23','540000006')");
    Check(sourceHeaders.All(h => currentRows.Any(c => Convert.ToString(c["Code"]) == Convert.ToString(h["WddCode"]) && Equals(c["U_Status"], h["Status"]))), "all live approval statuses synchronized");
    foreach (var row in currentRows)
    {
        var history = await Read("SELECT * FROM \"@PVI_APVH\" WHERE \"Code\"=?", row["U_Hash"]);
        Check(history.Count == 1 && row.Keys.Where(k => k.StartsWith("U_")).All(k => Equals(row[k], history[0][k])), "current/history match for request " + row["Code"]);
        if (Convert.ToString(row["U_AmtKnown"]) == "Y")
        {
            var table = Convert.ToString(row["U_ObjType"]) switch { "22" => "OPOR", "23" => "OQUT", "540000006" => "OPQT", _ => throw new Exception("Unsupported") };
            var sourceAmounts = int.TryParse(Convert.ToString(row["U_DocEntry"]), out var docKey) && docKey > 0
                ? await Read($"SELECT \"DocTotal\" FROM \"{table}\" WHERE \"DocEntry\"=?", docKey)
                : await Read("SELECT \"DocTotal\" FROM \"ODRF\" WHERE \"DocEntry\"=?", int.Parse(Convert.ToString(row["U_Draft"])!));
            if (sourceAmounts.Count > 0)
                Check(Convert.ToDecimal(sourceAmounts[0]["DocTotal"]) == Convert.ToDecimal(row["U_Amount"]), "saved amount matches source DocTotal for " + row["Code"]);
        }
        if (Convert.ToString(row["U_Source"]) == "N")
        {
            var prior = priorRows.Single(r => Equals(r["Code"], row["Code"]));
            Check(Equals(prior["U_Payload"], row["U_Payload"]), "deleted approval retains original XML for " + row["Code"]);
        }
        if (int.TryParse(Convert.ToString(row["U_DocEntry"]), out var entry) && entry > 0)
        {
            var table = Convert.ToString(row["U_ObjType"]) switch { "22" => "OPOR", "23" => "OQUT", "540000006" => "OPQT", _ => throw new Exception("Unsupported") };
            var docs = await Read($"SELECT \"DocNum\",\"DocStatus\",\"CANCELED\" FROM \"{table}\" WHERE \"DocEntry\"=?", entry);
            Check(docs.Count == 1 && Convert.ToString(docs[0]["DocNum"]) == Convert.ToString(row["U_DocNum"]) &&
                Convert.ToString(row["U_DocState"]) == (Convert.ToString(docs[0]["CANCELED"]) == "N" ? Convert.ToString(docs[0]["DocStatus"]) : "X"), "official document number/state match for " + row["Code"]);
        }
    }
    var firstCount = await Read("SELECT COUNT(*) AS \"N\" FROM \"@PVI_APVH\"");
    await repository.SynchronizeApprovalHistoryAsync(null, timeout.Token);
    foreach (var request in sourceHeaders.GroupBy(r => Convert.ToString(r["Status"])).Select(g => g.First()))
        await repository.SynchronizeApprovalHistoryAsync(Convert.ToInt32(request["WddCode"]), timeout.Token);
    var secondCount = await Read("SELECT COUNT(*) AS \"N\" FROM \"@PVI_APVH\"");
    Check(Equals(firstCount[0]["N"], secondCount[0]["N"]), "repeat full and per-request synchronization adds no duplicates");
    Console.WriteLine($"Synchronization integration passed: {sourceHeaders.Count} live requests, {currentRows.Count} current records.");
    return;
}
if (args.Length != 4 || (args[2] != "preserve" && args[2] != "convert")) throw new ArgumentException("Invalid test mode.");
var missingDraft = int.MaxValue;
Check((await Read("SELECT \"DocEntry\" FROM \"ODRF\" WHERE \"DocEntry\"=?", missingDraft)).Count == 0, "negative test draft does not exist");
var historyCountBeforeFailure = await Read("SELECT COUNT(*) AS \"N\" FROM \"@PVI_APVH\"");
try
{
    await service.SaveDraftToDocumentAsync(missingDraft, "unused-test-login", "unused-test-password");
    throw new Exception("Missing approval source was accepted.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("not fully approved; conversion was stopped."))
{
    Console.WriteLine("PASS missing approval source blocks conversion before Service Layer login");
}
var historyCountAfterFailure = await Read("SELECT COUNT(*) AS \"N\" FROM \"@PVI_APVH\"");
Check(Equals(historyCountBeforeFailure[0]["N"], historyCountAfterFailure[0]["N"]), "failed preservation adds no history records");
var draftEntry = int.Parse(args[3]);
Check(approved.Any(d => d.DraftEntry == draftEntry), "selected draft is fully approved");
var headers = await Read("SELECT \"WddCode\" FROM \"OWDD\" WHERE \"DraftEntry\" = ? ORDER BY \"WddCode\"", draftEntry);
var originalSteps = await Read("SELECT S.* FROM \"WDD1\" S JOIN \"OWDD\" W ON W.\"WddCode\"=S.\"WddCode\" WHERE W.\"DraftEntry\"=?", draftEntry);
await repository.PreserveApprovalHistoryAsync(draftEntry, timeout.Token);
var saved = await Read("SELECT * FROM \"@PVI_ARVC\" WHERE \"U_Draft\"=?", draftEntry.ToString());
Check(headers.All(h => saved.Any(s => Convert.ToString(s["Code"]) == Convert.ToString(h["WddCode"]))), "all draft approval requests persisted");
foreach (var row in saved.Where(s => headers.Any(h => Convert.ToString(h["WddCode"]) == Convert.ToString(s["Code"]))))
{
    var history = await Read("SELECT * FROM \"@PVI_APVH\" WHERE \"Code\"=?", row["U_Hash"]);
    Check(history.Count == 1, "immutable history exists for WddCode " + row["Code"]);
    Check(row.Keys.Where(k => k.StartsWith("U_")).All(k => Equals(row[k], history[0][k])), "all 28 current/history values match");
    var payload = XElement.Parse(Convert.ToString(row["U_Payload"])!);
    var sourceSteps = originalSteps.Where(s => Convert.ToString(s["WddCode"]) == Convert.ToString(row["Code"])).ToList();
    var payloadSteps = payload.Element("Steps")!.Elements("Step").ToList();
    Check(sourceSteps.Count == payloadSteps.Count, "approval step count preserved");
    foreach (var sourceStep in sourceSteps)
    {
        var preserved = payloadSteps.Single(s => (string?)s.Element("UserID") == Convert.ToString(sourceStep["UserID"]) && (string?)s.Element("SortId") == Convert.ToString(sourceStep["SortId"]));
        Check(sourceStep.All(p => XNode.DeepEquals(new XElement(p.Key, p.Value), preserved.Element(p.Key))), "all WDD1 source values preserved in XML");
    }
}
var beforeRetry = await Read("SELECT \"Code\" FROM \"@PVI_APVH\" WHERE \"U_Draft\"=?", draftEntry.ToString());
await repository.PreserveApprovalHistoryAsync(draftEntry, timeout.Token);
var afterRetry = await Read("SELECT \"Code\" FROM \"@PVI_APVH\" WHERE \"U_Draft\"=?", draftEntry.ToString());
Check(beforeRetry.Count == afterRetry.Count, "repeat preservation adds no duplicate history");
if (args[2] == "convert")
{
    await service.SaveDraftToDocumentAsync(draftEntry, cfg.UserName, cfg.Password);
    var table = approved.First(d => d.DraftEntry == draftEntry).ObjectType switch { "22" => "OPOR", "23" => "OQUT", "540000006" => "OPQT", _ => throw new Exception("Unsupported type") };
    var documents = await Read($"SELECT \"DocEntry\",\"DocNum\" FROM \"{table}\" WHERE \"draftKey\"=?", draftEntry);
    Check(documents.Count == 1, "official document exists with matching draftKey");
    Console.WriteLine($"Converted DraftEntry={draftEntry} => {table} DocEntry={documents[0]["DocEntry"]}, DocNum={documents[0]["DocNum"]}");
    foreach (var row in saved)
    {
        var history = await Read("SELECT \"U_Payload\" FROM \"@PVI_APVH\" WHERE \"Code\"=?", row["U_Hash"]);
        Check(history.Count == 1 && Equals(history[0]["U_Payload"], row["U_Payload"]), "approval history remains intact after conversion");
    }
}
Console.WriteLine("Integration validation completed.");
