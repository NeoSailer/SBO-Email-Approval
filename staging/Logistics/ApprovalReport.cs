using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace PVI.Logistics
{
    // 화면 계층은 문서 → 승인 요청이다. 승인 요청 선택 시 저장 XML의 단계 내역을 별도 표에 표시한다.
    public sealed class ApprovalReportRow
    {
        public string GroupKey;
        public bool IsDocument;
        public int RequestNumber;
        public Dictionary<string, object> Values;
        public string Text(string key) { return Snapshot.Text(Values, key); }
        public string Caption(bool expanded)
        {
            // 일반 공백은 SAP 그리드에서 줄어들 수 있으므로 전각 공백으로 계층 들여쓰기를 유지한다.
            return IsDocument ? (expanded ? "[−] " : "[+] ") + Text("DocType") : "\u3000\u3000#" + RequestNumber + " " + Text("DocType");
        }
    }
    public static class ApprovalReport
    {
        public static readonly string[] Columns = { "DocType", "DocNum", "DraftNum", "Creator", "TemplateName", "Status", "Remarks", "DraftKey", "DraftUser", "DraftDate", "DraftTime", "DraftNote", "Total" };
        public static readonly string[] StageColumns = { "StageName", "Approver", "Answer", "AnswerDate", "AnswerTime", "Remarks", "StageKey" };
        public static readonly string[] StageCaptions = { "단계", "승인자", "답변", "답변일", "답변 시간", "비고" };

        public static List<ApprovalReportRow> Group(IEnumerable<Dictionary<string, object>> source)
        {
            var rows = source.ToList();
            // 정식 전표 연결 이전/이후의 승인 요청도 동일 초안에서 생성된 전표라면 한 문서에 묶는다.
            var finalByDraft = rows.Where(r => Snapshot.Key(Snapshot.Text(r, "DraftKey")) > 0 && Snapshot.Key(Snapshot.Text(r, "DocEntry")) > 0)
                .GroupBy(r => Snapshot.Text(r, "ObjType") + ":" + Snapshot.Text(r, "DraftKey"))
                .Where(g => g.Select(r => Snapshot.Text(r, "DocEntry")).Distinct().Count() == 1)
                .ToDictionary(g => g.Key, g => Snapshot.Text(g.First(), "DocEntry"));
            Func<Dictionary<string, object>, string> identity = r => {
                string type = Snapshot.Text(r, "ObjType"), entry = Snapshot.Text(r, "DocEntry"), draft = Snapshot.Text(r, "DraftKey"), mapped;
                if (Snapshot.Key(entry) > 0) return type + ":D:" + entry;
                if (Snapshot.Key(draft) > 0) return finalByDraft.TryGetValue(type + ":" + draft, out mapped) ? type + ":D:" + mapped : type + ":F:" + draft;
                return type + ":A:" + Snapshot.Text(r, "ApprovalKey");
            };
            var result = new List<ApprovalReportRow>();
            foreach (var group in rows.GroupBy(identity)) {
                var latest = group.FirstOrDefault(r => Snapshot.Key(Snapshot.Text(r, "DocEntry")) > 0) ?? group.First();
                var parent = new Dictionary<string, object>(latest);
                parent["TemplateName"] = ""; parent["TemplateKey"] = ""; parent["Remarks"] = "";
                if (Snapshot.Key(Snapshot.Text(parent, "DocEntry")) > 0 && Snapshot.Text(parent, "Status") != "취소됨" && Snapshot.Text(parent, "Status") != "승인 및 생성") parent["Status"] = "생성";
                // 승인자 수/요청 횟수를 곱하거나 합산하지 않는다. 문서 총액은 상위 행에 단 한 번 표시한다.
                parent["Total"] = FormatTotal(Snapshot.Text(latest, "AmountKnown") == "Y" ? latest : group.FirstOrDefault(r => Snapshot.Text(r, "AmountKnown") == "Y") ?? latest);
                result.Add(new ApprovalReportRow { GroupKey = group.Key, IsDocument = true, Values = parent });
                int requestNumber = 0;
                foreach (var request in group) {
                    var child = new Dictionary<string, object>(request); child["Total"] = "";
                    result.Add(new ApprovalReportRow { GroupKey = group.Key, IsDocument = false, RequestNumber = ++requestNumber, Values = child });
                }
            }
            return result;
        }
        public static string FormatTotal(Dictionary<string, object> row)
        {
            if (Snapshot.Text(row, "AmountKnown") != "Y") return "미확인";
            decimal amount;
            if (!Decimal.TryParse(Snapshot.Text(row, "Amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) return "미확인";
            return amount.ToString("#,##0.######", CultureInfo.InvariantCulture) + " " + Snapshot.Text(row, "Currency");
        }
        public static List<string[]> Stages(string payload, IDictionary<string, string> currentNames)
        {
            var result = new List<string[]>();
            if (String.IsNullOrWhiteSpace(payload)) return result;
            var xml = XElement.Parse(payload);
            // 같은 단계에 승인자가 여럿 있어도 별도 단계로 번호를 새로 매기지 않는다.
            foreach (var step in xml.Descendants("Step").OrderBy(s => Sort((string)s.Element("SortId")))) {
                Func<string, string> value = fieldName => ((string)step.Element(fieldName) ?? "").Trim();
                string stageKey = value("StepCode"), name = value("StageName"), fallback;
                if (name == "") name = currentNames.TryGetValue(stageKey, out fallback) ? fallback : "이름이 보존되지 않은 단계";
                string answer = value("Status");
                // 보류에는 답변 시각을 만들어 넣지 않는다. SAP의 요청일을 답변일로 오인하지 않는다.
                bool answered = answer == "Y" || answer == "N";
                string authDate = Date(value("AuthUpdDat"));
                string date = answered ? (authDate == "" ? Date(value("UpdateDate")) : authDate) : "";
                string time = date == "" ? "" : (authDate == "" || value("AuthUpdTim") == "" ? MinuteTime(value("UpdateTime")) : Snapshot.TimeText(value("AuthUpdTim")));
                result.Add(new[] { name, value("ApproverName") == "" ? value("Login") : value("ApproverName"),
                    answer == "Y" ? "승인" : answer == "N" ? "거부" : answer == "W" ? "보류" : answer, date, time, value("Remarks"), stageKey });
            }
            return result;
        }
        static int Sort(string raw) { int value; return Int32.TryParse(raw, out value) ? value : Int32.MaxValue; }
        static string Date(string raw) { DateTime value; return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out value) && value.Year > 1900 ? value.ToString("yyyy.MM.dd") : ""; }
        // WDD1.UpdateTime은 HHmm, AuthUpdTim은 HHmmss이다.
        static string MinuteTime(string raw) { int value; return Int32.TryParse(raw, out value) && value >= 0 && value / 100 < 24 && value % 100 < 60 ? (value / 100).ToString("00") + ":" + (value % 100).ToString("00") : ""; }
    }
}
