using EmailApprovalServer.Services;
using EmailApprovalServer.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddProvider(new DailyFileLoggerProvider(
    Path.Combine(AppContext.BaseDirectory, "logs")));

builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddSingleton<HanaApprovalRepository>();
builder.Services.AddSingleton<DraftFinalizationService>();
builder.Services.AddHostedService<ApprovalNotificationWorker>();
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("EMAIL_APPROVAL_URLS") ?? "http://0.0.0.0:5050");

var app = builder.Build();
var buildVersion = typeof(Program).Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
    .Single().InformationalVersion;
app.Logger.LogInformation("EmailApprovalServer build {BuildVersion} starting", buildVersion);

app.MapPost("/approval/request", async (ApprovalNotificationRequest request, ApprovalService service, HttpContext context) =>
{
    if (!service.IsAllowedClient(context.Connection.RemoteIpAddress?.ToString()))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    try
    {
        await service.SendApprovalEmailAsync(request);
        return Results.Ok(new { message = "approval email sent" });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ApprovalService.ToAdminErrorMessage(ex) });
    }
});

app.MapGet("/approval/approve", (string requestId, ApprovalService service, HttpContext context) =>
    RenderDecisionPage(requestId, "approve", service, context));

app.MapGet("/approval/reject", (string requestId, ApprovalService service, HttpContext context) =>
    RenderDecisionPage(requestId, "reject", service, context));

app.MapPost("/approval/complete", async (HttpContext context, ApprovalService service, HanaApprovalRepository repository, DraftFinalizationService finalizationService) =>
{
    if (!service.IsAllowedClient(context.Connection.RemoteIpAddress?.ToString()))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var form = await context.Request.ReadFormAsync();
    var requestId = form["requestId"].ToString();
    var decision = form["decision"].ToString();
    var sapUser = form["sapUser"].ToString();
    var sapPassword = form["sapPassword"].ToString();
    var remarks = form["remarks"].ToString();

    if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(sapUser) || string.IsNullOrWhiteSpace(sapPassword))
    {
        return Results.Text(ApprovalHtml.RenderMessage("입력 오류", "SAP ID와 암호를 모두 입력해 주세요.", true), "text/html", statusCode: 400);
    }

    try
    {
        await service.SubmitSapApprovalAsync(requestId, decision, remarks, sapUser, sapPassword);
        var action = decision == "approve" ? "승인" : "거절";
        try
        {
            await repository.SynchronizeApprovalHistoryAsync(int.Parse(requestId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "SAP decision succeeded but history synchronization failed for WddCode {RequestId}", requestId);
            return Results.Text(ApprovalHtml.RenderMessage(
                $"{action} 완료", $"SAP {action} 처리는 완료되었지만 승인 이력 동기화에 실패했습니다. 5분 주기 작업에서 다시 시도합니다.",
                true, ApprovalService.ToAdminErrorMessage(ex)), "text/html");
        }
        if (decision == "approve" && int.TryParse(requestId, out var approvalRequestId))
        {
            try
            {
                var finalized = await finalizationService.FinalizeRequestIfApprovedAsync(
                    approvalRequestId, sapUser, sapPassword, context.RequestAborted);
                var message = finalized
                    ? "SAP 요청이 승인되었고 정식문서가 생성되었습니다."
                    : "SAP 요청이 정상적으로 승인되었습니다. 다음 승인 단계가 남아 있습니다.";
                return Results.Text(ApprovalHtml.RenderMessage("처리 완료", message), "text/html");
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex,
                    "Approval succeeded but immediate document finalization failed for WddCode {RequestId}",
                    approvalRequestId);
                return Results.Text(ApprovalHtml.RenderMessage(
                    "승인 완료",
                    "승인은 완료되었지만 정식문서 생성에 실패했습니다. 5분 주기 작업에서 다시 시도합니다.",
                    true,
                    ApprovalService.ToAdminErrorMessage(ex)), "text/html");
            }
        }
        return Results.Text(ApprovalHtml.RenderMessage("처리 완료", $"SAP 요청이 정상적으로 {action}되었습니다."), "text/html");
    }
    catch (Exception ex)
    {
        return Results.Text(ApprovalHtml.RenderMessage(
            "처리 실패",
            "SAP 요청을 처리하지 못했습니다. 아래 오류 정보를 관리자에게 전달해 주세요.",
            true,
            ApprovalService.ToAdminErrorMessage(ex)), "text/html", statusCode: 502);
    }
}).DisableAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = buildVersion }));
app.Run();

static IResult RenderDecisionPage(string requestId, string decision, ApprovalService service, HttpContext context)
{
    if (!service.IsAllowedClient(context.Connection.RemoteIpAddress?.ToString()))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Text(ApprovalHtml.RenderLogin(requestId, decision), "text/html");
}

public record ApprovalNotificationRequest(
    string RequestId,
    string ApproverId,
    string DocumentType,
    string DocumentNumber,
    string DraftNumber,
    string Requester,
    string CreatedDate,
    string DocumentTotal,
    string DraftRemarks,
    string ApproverEmail = "");
