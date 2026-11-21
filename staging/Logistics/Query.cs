using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PVI.Logistics
{
    public sealed class DocumentType
    {
        public string Code, Table, Name;
        public DocumentType(string code, string table, string name) { Code = code; Table = table; Name = name; }
        public static readonly DocumentType[] All = {
            new DocumentType("23", "OQUT", "판매견적"), new DocumentType("17", "ORDR", "판매오더"),
            new DocumentType("15", "ODLN", "납품"), new DocumentType("16", "ORDN", "판매반품"),
            new DocumentType("13", "OINV", "매출송장"), new DocumentType("14", "ORIN", "매출대변메모"),
            new DocumentType("203", "ODPI", "매출선수금"), new DocumentType("1470000113", "OPRQ", "구매요청"),
            new DocumentType("540000006", "OPQT", "구매견적"), new DocumentType("22", "OPOR", "구매오더"),
            new DocumentType("20", "OPDN", "구매입고"), new DocumentType("21", "ORPD", "구매반품"),
            new DocumentType("18", "OPCH", "매입송장"), new DocumentType("19", "ORPC", "매입대변메모"),
            new DocumentType("204", "ODPO", "매입선급금"), new DocumentType("67", "OWTR", "재고이전"),
            new DocumentType("1250000001", "OWTQ", "재고이전요청"), new DocumentType("59", "OIGN", "재고입고"),
            new DocumentType("60", "OIGE", "재고출고"), new DocumentType("234000031", "ORRR", "판매반품요청"),
            new DocumentType("234000032", "OPRR", "구매반품요청")
        };
    }
    public sealed class Filters
    {
        // 빈 조건은 제한하지 않는다. 같은 체크 그룹은 OR, 서로 다른 필터는 AND로 결합한다.
        public string User = "", From = "", To = "", Min = "", Max = "", Status = "", DocState = "";
        public string Requester = "", Approver = "";
        public readonly List<string> Statuses = new List<string>();
        public readonly List<string> Types = new List<string>();
        // HANA 문자열 리터럴의 작은따옴표를 이스케이프한다. 테이블명에는 사용하지 않는다.
        public static string Q(string value) { return "'" + value.Replace("'", "''") + "'"; }
        static DateTime? Date(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            DateTime value;
            if (!DateTime.TryParseExact(text.Trim(), new[] { "yyyy-MM-dd", "yyyyMMdd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                throw new ArgumentException("날짜는 yyyy-MM-dd 형식으로 입력하세요.");
            return value;
        }
        static decimal? Amount(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            decimal value;
            if (!Decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException("금액은 쉼표 없는 숫자로 입력하세요.");
            return value;
        }
        public string Where()
        {
            var conditions = new List<string>();
            var from = Date(From); var to = Date(To); var min = Amount(Min); var max = Amount(Max);
            if (from > to) throw new ArgumentException("시작일은 종료일보다 늦을 수 없습니다.");
            if (min > max) throw new ArgumentException("최소 금액은 최대 금액보다 클 수 없습니다.");
            if (!String.IsNullOrWhiteSpace(User)) {
                string loginXml = User.Trim().Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                string loginPattern = "%<Login>" + loginXml.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "</Login>%";
                conditions.Add("(\"U_User\" = " + Q(User.Trim()) + " OR \"U_Payload\" LIKE " + Q(loginPattern) + " ESCAPE '!')");
            }
            if (!String.IsNullOrWhiteSpace(Requester)) conditions.Add("\"U_User\" = " + Q(Requester.Trim()));
            if (!String.IsNullOrWhiteSpace(Approver)) {
                // 승인자는 저장된 단계 XML의 Login과 비교한다. LIKE 와일드카드도 일반 문자로 처리한다.
                string encoded = Approver.Trim().Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                string pattern = "%<Login>" + encoded.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "</Login>%";
                conditions.Add("\"U_Payload\" LIKE " + Q(pattern) + " ESCAPE '!'");
            }
            if (Statuses.Count > 0) {
                if (Statuses.Any(s => !new[] { "W", "Y", "N", "P", "A", "C" }.Contains(s))) throw new ArgumentException("전표 상태가 올바르지 않습니다.");
                conditions.Add("\"U_Status\" IN (" + String.Join(",", Statuses.Distinct().Select(Q)) + ")");
            }
            if (from.HasValue) conditions.Add("\"U_DocDate\" >= " + Q(from.Value.ToString("yyyy-MM-dd")));
            if (to.HasValue) conditions.Add("\"U_DocDate\" <= " + Q(to.Value.ToString("yyyy-MM-dd")));
            if (min.HasValue) conditions.Add("\"U_Amount\" >= " + min.Value.ToString(CultureInfo.InvariantCulture));
            if (max.HasValue) conditions.Add("\"U_Amount\" <= " + max.Value.ToString(CultureInfo.InvariantCulture));
            if (min.HasValue || max.HasValue) conditions.Add("\"U_AmtKnown\" = 'Y'");
            if (!String.IsNullOrWhiteSpace(Status)) {
                if (!new[] { "W", "Y", "N", "P", "A", "C" }.Contains(Status)) throw new ArgumentException("승인 상태가 올바르지 않습니다.");
                conditions.Add("\"U_Status\" = " + Q(Status));
            }
            if (!String.IsNullOrWhiteSpace(DocState)) {
                if (!new[] { "O", "C", "X", "?" }.Contains(DocState)) throw new ArgumentException("문서 상태가 올바르지 않습니다.");
                conditions.Add("\"U_DocState\" = " + Q(DocState));
            }
            if (Types.Count > 0) {
                if (Types.Any(t => !DocumentType.All.Any(d => d.Code == t))) throw new ArgumentException("문서 종류가 올바르지 않습니다.");
                conditions.Add("\"U_ObjType\" IN (" + String.Join(",", Types.Select(Q)) + ")");
            }
            return conditions.Count == 0 ? "" : " WHERE " + String.Join(" AND ", conditions);
        }
    }
}
