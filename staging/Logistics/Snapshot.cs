using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace PVI.Logistics
{
    // SAP COM과 분리한 순수 로직: 원본 소실/정식문서 전환을 DB 연결 없이 테스트한다.
    public static class Snapshot
    {
        // 승인 상태 리포트의 추가 열. 원본 초안 삭제 후에도 마지막 관찰값을 보존한다.
        public static readonly string[] ReportFields = { "DraftNum", "CreatorName", "TemplateKey", "TemplateName", "Remarks", "DraftUser", "DraftDate", "DraftTime", "DraftNote" };
        public static string Text(Dictionary<string, object> row, string key)
        { object value; return row != null && row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : ""; }
        public static int Key(string value) { int key; return Int32.TryParse(value, out key) && key > 0 ? key : 0; }
        public static string Draft(Dictionary<string, object> header, Dictionary<string, object> prior)
        {
            string draft = Text(header, "DraftEntry");
            if (Key(draft) > 0) return draft;
            if (Text(header, "IsDraft") == "Y" && Key(Text(header, "DocEntry")) > 0) return Text(header, "DocEntry");
            return Text(prior, "U_Draft");
        }
        public static Dictionary<string, object> Create(Dictionary<string, object> header, IEnumerable<Dictionary<string, object>> steps,
            Dictionary<string, object> prior, Dictionary<string, object> doc, bool final, Dictionary<string, object> draft = null)
        {
            string type = Text(header, "ObjType");
            var definition = DocumentType.All.FirstOrDefault(t => t.Code == type);
            var values = new Dictionary<string, object> {
                { "WddCode", Text(header, "WddCode") }, { "ObjType", type }, { "DocType", definition == null ? type : definition.Name },
                { "Draft", Draft(header, prior) }, { "DocEntry", "" }, { "DocNum", "" }, { "User", Text(header, "Login") },
                { "Status", Text(header, "Status") }, { "DocState", "?" }, { "DocDate", "" }, { "Amount", 0.0 },
                { "Currency", "" }, { "Detail", "N" }, { "Source", "Y" }, { "AmtKnown", "N" }
            };
            if (prior != null) foreach (string field in new[] { "DocEntry", "DocNum", "DocDate", "Amount", "Currency", "DocState", "AmtKnown" }) values[field] = prior["U_" + field];
            else if (header.ContainsKey("DocDate") && header["DocDate"] is DateTime) values["DocDate"] = ((DateTime)header["DocDate"]).ToString("yyyy-MM-dd");
            if (doc != null) ApplyDocument(values, doc, final);
            foreach (string field in ReportFields) values[field] = Text(prior, "U_" + field);
            values["CreatorName"] = Text(header, "LoginName") == "" ? Text(header, "Login") : Text(header, "LoginName");
            if (header.ContainsKey("WtmCode")) values["TemplateKey"] = Text(header, "WtmCode");
            if (Text(header, "TemplateName") != "") values["TemplateName"] = Text(header, "TemplateName");
            if (header.ContainsKey("Remarks")) values["Remarks"] = Text(header, "Remarks");
            if (draft == null && !final) draft = doc;
            ApplyDraft(values, draft);
            values["Payload"] = new XElement("Approval", Xml("Header", header), new XElement("Steps", steps.Select(s => Xml("Step", s)))).ToString(SaveOptions.DisableFormatting);
            return values;
        }
        static void ApplyDraft(Dictionary<string, object> values, Dictionary<string, object> draft)
        {
            if (draft != null) {
                values["DraftNum"] = Text(draft, "DocNum");
                values["DraftUser"] = Text(draft, "DraftUserName") == "" ? Text(draft, "DraftLogin") : Text(draft, "DraftUserName");
                values["DraftDate"] = DateText(draft, "UpdateDate");
                values["DraftTime"] = TimeText(Text(draft, "UpdateTS"));
                values["DraftNote"] = Text(draft, "Comments");
            }
        }
        static string DateText(Dictionary<string, object> row, string field)
        { object value; return row.TryGetValue(field, out value) && value is DateTime ? ((DateTime)value).ToString("yyyy-MM-dd") : ""; }
        public static string TimeText(string raw)
        {
            int number;
            if (!Int32.TryParse(raw, out number) || number < 0) return "";
            int hours = number / 10000, minutes = number / 100 % 100, seconds = number % 100;
            if (hours > 23 || minutes > 59 || seconds > 59) return "";
            return hours.ToString("00") + ":" + minutes.ToString("00") + ":" + seconds.ToString("00");
        }
        public static Dictionary<string, object> Archive(Dictionary<string, object> prior, Dictionary<string, object> doc, Dictionary<string, object> draft = null)
        {
            // 원본이 없다는 표시만 갱신한다. 관찰하지 못한 최종 승인 상태를 추측해 만들지 않는다.
            var values = prior.Where(p => p.Key.StartsWith("U_", StringComparison.Ordinal) && p.Key != "U_Hash" && p.Key != "U_Content" && p.Key != "U_Captured").ToDictionary(p => p.Key.Substring(2), p => p.Value);
            foreach (string field in ReportFields) values[field] = Text(prior, "U_" + field);
            // 이전 버전이 XML로 이미 보존한 값은 원본 삭제 후에도 복구할 수 있다.
            var payload = Text(prior, "U_Payload");
            if (payload != "") {
                var header = XElement.Parse(payload).Element("Header");
                if (header != null) {
                    if (Text(values, "TemplateKey") == "") values["TemplateKey"] = (string)header.Element("WtmCode") ?? "";
                    if (Text(values, "Remarks") == "") values["Remarks"] = (string)header.Element("Remarks") ?? "";
                }
            }
            if (Text(values, "CreatorName") == "") values["CreatorName"] = Text(prior, "U_User");
            values["Source"] = "N"; values["Detail"] = "N";
            if (doc != null) ApplyDocument(values, doc, true);
            ApplyDraft(values, draft);
            return values;
        }
        static void ApplyDocument(Dictionary<string, object> values, Dictionary<string, object> doc, bool final)
        {
            values["DocEntry"] = final ? Text(doc, "DocEntry") : "";
            values["DocNum"] = Text(doc, "DocNum"); values["DocDate"] = Convert.ToDateTime(doc["DocDate"]).ToString("yyyy-MM-dd");
            values["Amount"] = Convert.ToDouble(doc["DocTotal"], CultureInfo.InvariantCulture);
            values["Currency"] = Text(doc, "DocCur"); values["DocState"] = Text(doc, "CANCELED") == "N" ? Text(doc, "DocStatus") : "X";
            values["Detail"] = "Y"; values["AmtKnown"] = "Y";
        }
        public static string Fingerprint(Dictionary<string, object> values)
        { return Digest(Xml("Snapshot", values).ToString(SaveOptions.DisableFormatting)); }
        public static bool CanAdvance(Dictionary<string, object> observed, Dictionary<string, object> current)
        {
            // 다른 클라이언트가 먼저 갱신한 상태를 오래된 관찰 결과로 덮어쓰지 않도록 비교한다.
            if (observed == null || current == null) return observed == null && current == null;
            return Text(observed, "U_Hash") == Text(current, "U_Hash");
        }
        public static string EventKey(string predecessor, string content) { return Digest(predecessor + ":" + content); }
        static string Digest(string value)
        { using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", ""); }
        static XElement Xml(string name, Dictionary<string, object> row)
        { return new XElement(name, row.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new XElement(p.Key, p.Value))); }
    }
    public sealed class DocumentIndex
    {
        readonly Dictionary<string, Dictionary<string, object>> entries = new Dictionary<string, Dictionary<string, object>>();
        readonly Dictionary<string, List<Dictionary<string, object>>> byDraft = new Dictionary<string, List<Dictionary<string, object>>>();
        readonly Dictionary<string, Dictionary<string, object>> drafts = new Dictionary<string, Dictionary<string, object>>();
        public void AddFinal(string type, Dictionary<string, object> doc)
        {
            string key = type + ":" + Snapshot.Text(doc, "DocEntry");
            if (entries.ContainsKey(key)) return;
            entries[key] = doc;
            if (Snapshot.Key(Snapshot.Text(doc, "draftKey")) == 0) return;
            key = type + ":" + Snapshot.Text(doc, "draftKey");
            if (!byDraft.ContainsKey(key)) byDraft[key] = new List<Dictionary<string, object>>();
            byDraft[key].Add(doc);
        }
        public void AddDraft(Dictionary<string, object> doc)
        { drafts[Snapshot.Text(doc, "ObjType") + ":" + Snapshot.Text(doc, "DocEntry")] = doc; }
        public Dictionary<string, object> FindDraft(string type, string key)
        { Dictionary<string, object> doc; return drafts.TryGetValue(type + ":" + key, out doc) ? doc : null; }
        public Dictionary<string, object> Find(string type, string entry, string draft, out bool final)
        {
            Dictionary<string, object> doc; List<Dictionary<string, object>> candidates;
            final = true;
            if (Snapshot.Key(entry) > 0 && entries.TryGetValue(type + ":" + entry, out doc)) return doc;
            if (Snapshot.Key(draft) > 0 && byDraft.TryGetValue(type + ":" + draft, out candidates) && candidates.Count == 1) return candidates[0];
            final = false;
            return drafts.TryGetValue(type + ":" + draft, out doc) ? doc : null;
        }
    }
}
