using System.Net;

namespace EmailApprovalServer.Services;

public static class ApprovalHtml
{
    public static string RenderLogin(string requestId, string decision)
    {
        var safeRequestId = WebUtility.HtmlEncode(requestId);
        var safeDecision = decision == "reject" ? "reject" : "approve";
        var action = safeDecision == "approve" ? "승인" : "거절";
        var accent = safeDecision == "approve" ? "#86c89a" : "#e99a9a";

        return Page($$"""
            <main class="card">
              <div class="brand">TISSIN · SAP Business One</div>
              <h1>SAP Business One 계정 로그인</h1>
              <p class="description">요청을 <strong style="color:{{accent}}">{{action}}</strong>하려면 SAP 계정으로 로그인해 주세요.</p>
              <form method="post" action="/approval/complete" autocomplete="off">
                <input type="hidden" name="requestId" value="{{safeRequestId}}">
                <input type="hidden" name="decision" value="{{safeDecision}}">
                <label for="sapUser">SAP ID</label>
                <input id="sapUser" name="sapUser" autocomplete="username" required autofocus>
                <label for="sapPassword">암호</label>
                <input id="sapPassword" name="sapPassword" type="password" autocomplete="current-password" required>
                <label for="remarks">비고</label>
                <textarea id="remarks" name="remarks" rows="4" placeholder="처리 의견을 입력해 주세요."></textarea>
                <div class="actions">
                  <a class="cancel" href="javascript:history.back()">취소</a>
                  <button type="submit" style="background:{{accent}}">{{action}}하기</button>
                </div>
              </form>
              <p class="security">입력한 계정 정보는 SAP 로그인 및 요청 처리에만 사용되며 저장되지 않습니다.</p>
            </main>
            """);
    }

    public static string RenderMessage(string title, string message, bool isError = false, string? details = null)
    {
        var statusClass = isError ? "status error" : "status";
        var icon = isError ? "!" : "✓";
        var detailHtml = string.IsNullOrWhiteSpace(details)
            ? string.Empty
            : $"<div class=\"error-details\"><strong>관리자 전달용 오류 정보</strong><code>{WebUtility.HtmlEncode(details)}</code></div>";

        return Page($$"""
        <main class="card message"><div class="{{statusClass}}">{{icon}}</div><h1>{{WebUtility.HtmlEncode(title)}}</h1>
        <p>{{WebUtility.HtmlEncode(message)}}</p>{{detailHtml}}</main>
        """);
    }

    private static string Page(string content) => $$$"""
        <!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>SAP Business One</title><style>
        *{box-sizing:border-box}body{margin:0;background:#f4f6f9;color:#172033;font-family:"Segoe UI","Noto Sans KR",sans-serif;min-height:100vh;display:grid;place-items:center;padding:24px}
        .card{width:min(100%,520px);background:#fff;border:1px solid #e1e6ee;border-radius:18px;padding:42px;box-shadow:0 20px 55px rgba(22,34,58,.12)}
        .brand{font-size:13px;font-weight:700;letter-spacing:.08em;color:#2563eb;text-transform:uppercase;margin-bottom:16px}h1{font-size:28px;line-height:1.25;margin:0 0 12px}.description{color:#667085;margin:0 0 30px;line-height:1.6}
        label{display:block;font-weight:650;margin:18px 0 8px}input,textarea{width:100%;border:1px solid #cbd3df;border-radius:10px;padding:13px 14px;font:inherit;background:#fff;transition:.15s}textarea{resize:vertical}input:focus,textarea:focus{outline:3px solid #dcfce7;border-color:#86c89a}
        .actions{display:flex;justify-content:flex-end;align-items:center;gap:12px;margin-top:28px}.cancel{color:#475467;text-decoration:none;padding:12px 16px}button{border:0;border-radius:10px;color:#fff;padding:13px 22px;font:inherit;font-weight:700;cursor:pointer}.security{font-size:12px;color:#98a2b3;margin:24px 0 0;line-height:1.5}.message{text-align:center}.status{margin:auto auto 20px;width:52px;height:52px;border-radius:50%;display:grid;place-items:center;background:#dcfce7;color:#15803d;font-size:26px}.status.error{background:#fee2e2;color:#b91c1c}.error-details{margin-top:24px;padding:16px;text-align:left;background:#fff1f2;border:1px solid #fecdd3;border-radius:10px;color:#881337}.error-details strong{display:block;margin-bottom:9px}.error-details code{display:block;white-space:pre-wrap;overflow-wrap:anywhere;font-family:Consolas,monospace;font-size:13px;line-height:1.55}
        @media(max-width:560px){.card{padding:28px 22px;border-radius:14px}h1{font-size:24px}}
        </style></head><body>{{{content}}}</body></html>
        """;
}
