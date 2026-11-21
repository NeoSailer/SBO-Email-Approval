using System.Xml.Linq;
using EmailApprovalServer.Services;

internal static class ApprovalHistoryTests
{
    public static void Run()
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        var header = new Dictionary<string, object>
        {
            ["WddCode"] = 42, ["DraftEntry"] = 652, ["ObjType"] = "22", ["IsDraft"] = "Y",
            ["Status"] = "Y", ["Login"] = "creator", ["LoginName"] = "생성자",
            ["WtmCode"] = 7, ["TemplateName"] = "구매 승인", ["Remarks"] = "확인 & 승인"
        };
        var draft = new Dictionary<string, object>
        {
            ["DocEntry"] = 652, ["DocNum"] = 395, ["DocDate"] = new DateTime(2026, 9, 19),
            ["DocTotal"] = 1234.5m, ["DocCur"] = "USD", ["DocStatus"] = "O", ["CANCELED"] = "N",
            ["DraftUserName"] = "수정자", ["UpdateDate"] = new DateTime(2026, 9, 19),
            ["UpdateTS"] = 91503, ["Comments"] = "초안 <비고>"
        };
        var steps = new[] { new Dictionary<string, object>
        {
            ["StepCode"] = 17, ["SortId"] = 1, ["Login"] = "approver", ["ApproverName"] = "승인자",
            ["StageName"] = "부서장 승인", ["Status"] = "Y", ["Remarks"] = "검토 <완료> & 승인",
            ["AuthUpdDat"] = new DateTime(2026, 9, 19), ["AuthUpdTim"] = 101112
        } };
        var snapshot = ApprovalHistorySnapshot.Create(header, steps, null!, draft, false, draft);
        Check(snapshot.Count == 25, "Snapshot must supply 25 fields plus three persistence fields.");
        Check((string)snapshot["DocEntry"] == "" && (string)snapshot["Draft"] == "652", "Draft key must not become the official document key.");
        Check((string)snapshot["DraftNum"] == "395" && (string)snapshot["TemplateKey"] == "7", "Report keys were lost.");
        Check((string)snapshot["DraftTime"] == "09:15:03" && (string)snapshot["Currency"] == "USD", "Draft metadata was lost.");
        var xml = XElement.Parse((string)snapshot["Payload"]);
        var step = xml.Element("Steps")!.Element("Step")!;
        Check((string?)step.Element("StageName") == "부서장 승인" && (string?)step.Element("StepCode") == "17", "Stage identity must survive source deletion.");
        Check((string?)step.Element("Remarks") == "검토 <완료> & 승인" && (string?)step.Element("AuthUpdTim") == "101112", "Decision details must survive XML serialization.");
        var fingerprint = ApprovalHistorySnapshot.Fingerprint(snapshot);
        var prior = snapshot.ToDictionary(p => "U_" + p.Key, p => p.Value);
        prior["U_Content"] = fingerprint;
        prior["U_Hash"] = ApprovalHistorySnapshot.EventKey("", fingerprint);
        var retry = ApprovalHistorySnapshot.Create(header, steps, prior, draft, false, draft);
        Check(ApprovalHistorySnapshot.StoredValuesMatch(retry, prior), "Matching persisted fields must remain unchanged.");
        var brokenAmount = new Dictionary<string, object>(prior) { ["U_Amount"] = 0m };
        Check(!ApprovalHistorySnapshot.StoredValuesMatch(retry, brokenAmount), "Correct hash with an incorrect stored amount must trigger repair.");
        Check(ApprovalHistorySnapshot.Fingerprint(retry) == fingerprint, "Retry must retain the same fingerprint.");
        steps[0]["Remarks"] = "수정된 승인 비고";
        Check(ApprovalHistorySnapshot.Fingerprint(ApprovalHistorySnapshot.Create(header, steps, prior, draft, false, draft)) != fingerprint,
            "Changed decision details must produce a new history event.");
        var finalDocument = new Dictionary<string, object>(draft) { ["DocEntry"] = 900, ["DocNum"] = 777, ["DocStatus"] = "C" };
        var archived = ApprovalHistorySnapshot.Archive(prior, finalDocument);
        Check((string)archived["DocEntry"] == "900" && (string)archived["DocNum"] == "777" && (string)archived["DocState"] == "C",
            "Converted document identity and state must be reflected after source deletion.");
        Check((string)archived["Source"] == "N" && Equals(archived["Payload"], snapshot["Payload"]), "Missing OWDD must retain the complete approval XML.");
        Check(ApprovalHistorySnapshot.ReportFields.All(f => Equals(snapshot[f], archived[f])), "Missing draft must retain report metadata.");
        var archivedPrior = archived.ToDictionary(p => "U_" + p.Key, p => p.Value);
        Check(ApprovalHistorySnapshot.Fingerprint(ApprovalHistorySnapshot.Archive(archivedPrior, finalDocument)) == ApprovalHistorySnapshot.Fingerprint(archived),
            "Repeated periodic scans of a deleted source must not add duplicate events.");
        finalDocument["CANCELED"] = "Y";
        Check((string)ApprovalHistorySnapshot.Archive(archivedPrior, finalDocument)["DocState"] == "X", "Canceled final documents must synchronize their state.");
        var missingSteps = ApprovalHistorySnapshot.Create(header, Array.Empty<Dictionary<string, object>>(), prior, finalDocument, true);
        Check(XNode.DeepEquals(XElement.Parse((string)missingSteps["Payload"]).Element("Steps"), xml.Element("Steps")),
            "Missing WDD1 with surviving OWDD must retain previously captured decisions.");
        foreach (var status in new[] { "W", "N", "Y" })
        {
            header["Status"] = status;
            var changed = ApprovalHistorySnapshot.Create(header, steps, prior, draft, false, draft);
            Check((string)changed["Status"] == status, "Pending/rejected/approved status must synchronize.");
        }
        // SAP 변환 버그: 초안/승인 원본이 모두 없어도 완료 기록은 삭제하거나 비우지 않는다.
        foreach (var completedStatus in new[] { "Y", "P", "A" })
        {
            var completed = new Dictionary<string, object>(prior) { ["U_Status"] = completedStatus };
            var retained = ApprovalHistorySnapshot.Reconcile(null!, Array.Empty<Dictionary<string, object>>(), completed, null!, null!);
            Check(retained.Where(p => p.Key != "Source").All(p => Equals(p.Value, completed["U_" + p.Key])),
                "All preserved fields must survive deletion of every source table.");
            Check((string)retained["Status"] == completedStatus && (string)retained["Detail"] == "Y", "Source deletion must not demote a completed approval.");
            var damagedHeader = new Dictionary<string, object>(header) { ["Status"] = "W", ["Remarks"] = "", ["Login"] = "", ["LoginName"] = "" };
            var partial = ApprovalHistorySnapshot.Reconcile(damagedHeader, steps, completed, null!, null!);
            Check((string)partial["Status"] == completedStatus && Equals(partial["Payload"], completed["U_Payload"]) && Equals(partial["Remarks"], completed["U_Remarks"]),
                "Surviving incomplete OWDD must not overwrite a completed snapshot after draft deletion.");
            var linked = ApprovalHistorySnapshot.Reconcile(damagedHeader, Array.Empty<Dictionary<string, object>>(), completed, finalDocument, null!);
            Check((string)linked["DocEntry"] == "900" && (string)linked["Status"] == completedStatus && Equals(linked["Payload"], completed["U_Payload"]),
                "Official document enrichment must preserve the approval decision and XML.");
            var linkedPrior = linked.ToDictionary(p => "U_" + p.Key, p => p.Value);
            var later = ApprovalHistorySnapshot.Reconcile(null!, Array.Empty<Dictionary<string, object>>(), linkedPrior, null!, null!);
            Check((string)later["DocEntry"] == "900" && (string)later["DocNum"] == "777" && Equals(later["Amount"], linked["Amount"]),
                "A later missing document lookup must not clear the preserved official document identity or amount.");
            var laterPrior = later.ToDictionary(p => "U_" + p.Key, p => p.Value);
            Check(ApprovalHistorySnapshot.Fingerprint(later) == ApprovalHistorySnapshot.Fingerprint(
                ApprovalHistorySnapshot.Reconcile(null!, Array.Empty<Dictionary<string, object>>(), laterPrior, null!, null!)),
                "Repeated scans of deleted sources must keep the same completed snapshot.");
        }
        Console.WriteLine("All approval history snapshot tests passed.");
    }
}
