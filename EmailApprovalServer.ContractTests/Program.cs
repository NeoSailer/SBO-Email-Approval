using System.Text.Json;
using EmailApprovalServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static JsonElement Create(string decision, string remarks)
{
    using var document = JsonDocument.Parse(SapApprovalRequestFactory.CreateJson("9", decision, remarks));
    return document.RootElement.Clone();
}

var approved = Create("approve", "looks good");
var approvedRequest = approved.GetProperty("ApprovalRequest");
var approvedDecision = approvedRequest.GetProperty("ApprovalRequestDecisions")[0];
Assert(approvedRequest.GetProperty("Code").GetInt32() == 9, "Code must be a JSON number.");
Assert(approvedDecision.GetProperty("Status").GetString() == "ardApproved", "Approve status is invalid.");
Assert(approvedDecision.GetProperty("Remarks").GetString() == "looks good", "Remarks were not preserved.");
Assert(!approvedRequest.TryGetProperty("Decision", out _), "Invalid Decision property must not be sent.");

var rejected = Create("reject", "needs changes");
Assert(rejected.GetProperty("ApprovalRequest").GetProperty("ApprovalRequestDecisions")[0]
    .GetProperty("Status").GetString() == "ardNotApproved", "Reject status is invalid.");

try
{
    SapApprovalRequestFactory.CreateJson("not-a-number", "approve", null);
    throw new InvalidOperationException("Invalid requestId was accepted.");
}
catch (ArgumentException)
{
}

try
{
    SapApprovalRequestFactory.CreateJson("9", "invalid", null);
    throw new InvalidOperationException("Invalid decision was accepted.");
}
catch (ArgumentException)
{
}

Console.WriteLine("All SAP approval contract tests passed.");

var page = ApprovalHtml.RenderLogin("9", "approve");
Assert(page.Contains("SAP Business One 계정 로그인"), "Login heading is missing.");
Assert(page.Contains("name=\"sapUser\""), "SAP ID field is missing.");
Assert(page.Contains("name=\"sapPassword\""), "Password field is missing.");
Assert(page.Contains("name=\"remarks\""), "Remarks field is missing.");

var sapError = new InvalidOperationException("SAP Service Layer request failed: 400 {\"error\":{\"code\":-1000,\"message\":{\"value\":\"Invalid property\"}}}");
var adminError = ApprovalService.ToAdminErrorMessage(sapError);
Assert(adminError.Contains("HTTP: 400") && adminError.Contains("SAP code: -1000") && adminError.Contains("Invalid property"), "SAP error details were not extracted.");
var errorPage = ApprovalHtml.RenderMessage("처리 실패", "관리자에게 전달해 주세요.", true, adminError);
Assert(errorPage.Contains("status error") && errorPage.Contains("관리자 전달용 오류 정보"), "Error page styling or details are missing.");
var requestIdError = ApprovalService.ToAdminErrorMessage(new ArgumentException("invalid", "requestId"));
Assert(requestIdError.Contains("OWDD.WddCode") && !requestIdError.Contains("ArgumentException"), "Request ID error must be administrator-friendly.");

var notification = new ApprovalNotificationRequest(
    "9", "pcb03", "구매 오더", "395", "652", "pcb01",
    "2026.08.12", "3,927 KRW", "납기 확인 & 발주 요청");
var email = ApprovalService.BuildApprovalEmailBody(notification, "http://server/approve", "http://server/reject");
Assert(email.Contains("안녕하세요 <strong>pcb03</strong>"), "Approver greeting is missing.");
Assert(email.Contains("구매 오더") && email.Contains("395") && email.Contains("652") && email.Contains("pcb01"), "Document details are missing.");
Assert(email.Contains("2026.08.12") && email.Contains("3,927 KRW"), "Date or document total is missing.");
Assert(email.Contains("납기 확인 &amp; 발주 요청"), "Draft remarks must be present and HTML encoded.");
Assert(email.Contains(">승인</a>") && email.Contains(">거절</a>"), "Email action buttons are missing.");
Assert(email.Contains("background:#a8d8b9"), "Approve button must use pastel green.");
Assert(email.Contains("background:#f3b6b6"), "Reject button must use pastel red.");
var ccAddresses = ApprovalService.ParseMailAddresses("jaehee.yoon@tissin.co.kr; second@example.com");
Assert(ccAddresses.Count == 2 && ccAddresses[0].Address == "jaehee.yoon@tissin.co.kr", "CC address parsing is invalid.");

var labels = new[] { "전표유형", "전표번호", "초안키", "생성자", "생성일", "전표 총계", "초안비고" };
var previousLabelPosition = -1;
foreach (var label in labels)
{
    var labelPosition = email.IndexOf(label, StringComparison.Ordinal);
    Assert(labelPosition > previousLabelPosition, $"Email field '{label}' is missing or out of order.");
    previousLabelPosition = labelPosition;
}

Console.WriteLine("All UI and email template tests passed.");

Assert(ApprovalNotificationWorker.TryGetDueReminderSlot(
    new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.FromHours(9)), new[] { "10:00", "15:00" }, out var morningSlot) &&
    morningSlot == "2026-08-12:10:00", "Weekday 10:00 reminder must run.");
Assert(ApprovalNotificationWorker.TryGetDueReminderSlot(
    new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.FromHours(9)), new[] { "10:00", "15:00" }, out _),
    "Weekday 15:00 reminder must run.");
Assert(!ApprovalNotificationWorker.TryGetDueReminderSlot(
    new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.FromHours(9)), new[] { "10:00", "15:00" }, out _),
    "Weekend reminder must not run.");
Assert(!ApprovalNotificationWorker.TryGetDueReminderSlot(
    new DateTimeOffset(2026, 8, 12, 10, 1, 0, TimeSpan.FromHours(9)), new[] { "10:00", "15:00" }, out _),
    "Reminder must not run outside its configured minute.");

var pendingApproval = new PendingApproval(22, 339, "pcb03", "구매 오더", "305", "Support",
    new DateTime(2026, 8, 11), 3927m, "KRW", "초안 비고", "pcb03@example.com");
var pendingNotification = pendingApproval.ToNotification();
Assert(pendingNotification.RequestId == "22" && pendingNotification.DraftNumber == "339", "Pending approval key mapping is invalid.");
Assert(pendingNotification.DocumentTotal == "3,927 KRW" && pendingNotification.CreatedDate == "2026.08.11", "Pending approval display formatting is invalid.");
Assert(pendingNotification.ApproverEmail == "pcb03@example.com", "Approver email mapping is invalid.");

Console.WriteLine("All scheduler and pending approval mapping tests passed.");

var draftDetail = new DraftDetail("VA0005", "(주)다원이엔지", "286", 1060000m, "KRW",
    new List<DraftLine> { new("EC01-0003", 1m, 1060000m) });
var detailedEmail = ApprovalService.BuildApprovalEmailBody(
    notification, "http://server/approve", "http://server/reject", draftDetail);
Assert(detailedEmail.Contains("[전표 상세]") && detailedEmail.Contains("VA0005") && detailedEmail.Contains("(주)다원이엔지"), "Email document header details are missing.");
var detailSectionStart = detailedEmail.IndexOf("[전표 상세]", StringComparison.Ordinal);
var actionSectionStart = detailedEmail.IndexOf(">승인</a>", detailSectionStart, StringComparison.Ordinal);
var detailSection = detailedEmail[detailSectionStart..actionSectionStart];
Assert(!detailSection.Contains("전표번호"), "Document number must not appear in the document detail section.");
Assert(detailedEmail.Contains("EC01-0003") && detailedEmail.Contains("품목번호") && detailedEmail.Contains("수량") && detailedEmail.Contains("단가"), "Email item details are missing.");
Assert(detailedEmail.Contains("1,060,000.00 KRW"), "Email document total is missing or incorrectly formatted.");

Console.WriteLine("All email document detail tests passed.");

var approvalService = new ApprovalService(
    new ConfigurationBuilder().Build(),
    NullLogger<ApprovalService>.Instance);
var missingEmailNotification = notification with { ApproverEmail = string.Empty };
var missingEmailSent = await approvalService.SendAutomatedApprovalEmailAsync(missingEmailNotification, draftDetail);
Assert(!missingEmailSent, "An approver without OUSR.E_Mail must be skipped without sending.");

Console.WriteLine("All missing approver email tests passed.");
