using System;
using System.Linq;

namespace PVI.Logistics
{
    // 기본 승인 상태 리포트와 같은 열 순서. 링크용 내부 키는 뒤에 두고 화면에서 숨긴다.
    public static class ReportQuery
    {
        public static readonly string[] Captions = { "전표 유형", "전표 번호", "전표 초안 번호", "생성자", "템플릿", "상태", "비고", "초안 키", "초안 갱신자", "초안 갱신일", "초안 갱신 시간", "초안 비고" };
        public static readonly string Select = "SELECT \"U_DocType\" AS \"DocType\", CASE WHEN COALESCE(\"U_DocEntry\",'') <> '' THEN \"U_DocNum\" ELSE '' END AS \"DocNum\", " +
            "CASE WHEN COALESCE(\"U_DocEntry\",'') = '' THEN \"U_DraftNum\" ELSE '' END AS \"DraftNum\", COALESCE(NULLIF(\"U_CreatorName\",''),\"U_User\") AS \"Creator\", COALESCE(NULLIF(\"U_TemplateName\",''),\"U_TemplateKey\") AS \"TemplateName\", " +
            "CASE \"U_Status\" WHEN 'W' THEN '보류' WHEN 'Y' THEN '승인' WHEN 'N' THEN '거부' WHEN 'P' THEN '생성' WHEN 'A' THEN '승인 및 생성' WHEN 'C' THEN '취소됨' ELSE \"U_Status\" END AS \"Status\", " +
            "\"U_Remarks\" AS \"Remarks\", \"U_Draft\" AS \"DraftKey\", \"U_DraftUser\" AS \"DraftUser\", \"U_DraftDate\" AS \"DraftDate\", \"U_DraftTime\" AS \"DraftTime\", \"U_DraftNote\" AS \"DraftNote\", " +
            "\"U_DocEntry\" AS \"DocEntry\", \"U_ObjType\" AS \"ObjType\", \"U_TemplateKey\" AS \"TemplateKey\", \"U_Hash\" AS \"Hash\", \"U_WddCode\" AS \"ApprovalKey\", " +
            // U_Amount는 DocTotal(회사 통화)이다. 외화 문서의 DocCur를 붙여 다른 통화 금액으로 오인하지 않는다.
            // 원본이 존재하면 실제 DocTotal을 우선한다. 삭제된 원본만 보존 금액으로 표시한다.
            "COALESCE(F.\"DocTotal\", D.\"DocTotal\", C.\"U_Amount\") AS \"Amount\", (SELECT \"MainCurncy\" FROM \"OADM\") AS \"Currency\", " +
            "CASE WHEN F.\"DocTotal\" IS NOT NULL OR D.\"DocTotal\" IS NOT NULL THEN 'Y' ELSE C.\"U_AmtKnown\" END AS \"AmountKnown\" FROM \"@PVI_ARVC\" C " +
            "LEFT JOIN \"ODRF\" D ON TO_NVARCHAR(D.\"DocEntry\")=C.\"U_Draft\" AND D.\"ObjType\"=C.\"U_ObjType\" " +
            "LEFT JOIN (" + String.Join(" UNION ALL ", DocumentType.All.Select(t => "SELECT '" + t.Code + "' AS \"Type\", \"DocEntry\", \"DocTotal\" FROM \"" + t.Table + "\"")) + ") F " +
            "ON TO_NVARCHAR(F.\"DocEntry\")=C.\"U_DocEntry\" AND F.\"Type\"=C.\"U_ObjType\"";
    }
}
