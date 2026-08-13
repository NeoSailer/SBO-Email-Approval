using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Globalization;

namespace EmailApprovalServer.Services;

// 승인 요청 처리와 외부 시스템 연동을 담당하는 서비스 클래스다.
public class ApprovalService
{
    // 설정값과 로깅을 주입받기 위한 필드다.
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApprovalService> _logger;

    // 생성자: DI로 설정과 로거를 받아 저장한다.
    public ApprovalService(IConfiguration configuration, ILogger<ApprovalService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    // 실행 파일 기준으로 XML 설정 파일 경로를 반환한다.
    public string LoadConfigPath() => Path.Combine(AppContext.BaseDirectory, "Config", "appsettings.xml");

    // XML 설정 파일을 읽어 ConfigData 객체로 변환한다.
    public ConfigData LoadConfig()
    {
        // 설정 파일 경로를 확인한다.
        var path = LoadConfigPath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("XML config file was not found", path);
        }

        // XML 문서를 로드한다.
        var doc = new XmlDocument();
        doc.Load(path);

        // 루트와 각 섹션을 가져온다.
        var root = doc.DocumentElement!;
        var general = root["General"]!;
        var sap = root["Sap"]!;
        var mail = root["Mail"]!;
        var reminder = root["Reminder"]!;
        var hana = root["Hana"]!;

        // 각 값을 ConfigData에 매핑한다.
        return new ConfigData
        {
            BaseUrl = GetValue(general, "BaseUrl"),
            AllowedNetworkPrefix = GetValue(general, "AllowedNetworkPrefix"),
            AllowPrivateNetwork = bool.Parse(GetValue(general, "AllowPrivateNetwork")),
            EnableSsl = bool.Parse(GetValue(general, "EnableSsl")),
            ServiceLayerUrl = GetValue(sap, "ServiceLayerUrl"),
            CompanyDb = GetValue(sap, "CompanyDb"),
            UserName = GetValue(sap, "UserName"),
            Password = GetValue(sap, "Password"),
            SessionTimeoutSeconds = int.Parse(GetValue(sap, "SessionTimeoutSeconds")),
            SmtpHost = GetValue(mail, "SmtpHost"),
            SmtpPort = int.Parse(GetValue(mail, "SmtpPort")),
            SmtpUser = GetValue(mail, "SmtpUser"),
            SmtpPassword = GetValue(mail, "SmtpPassword"),
            FromAddress = GetValue(mail, "FromAddress"),
            MailEnableSsl = bool.Parse(GetValue(mail, "EnableSsl")),
            TestRecipient = GetValue(mail, "TestRecipient"),
            CcAddress = GetValue(mail, "CcAddress"),
            ReminderEnabled = bool.Parse(GetValue(reminder, "Enabled")),
            ReminderTimes = GetValue(reminder, "RunAtTimes")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .ToArray(),
            ReminderSubject = GetValue(reminder, "TemplateSubject"),
            ReminderBody = GetValue(reminder, "TemplateBody"),
            HanaHost = GetValue(hana, "Host"),
            HanaPort = int.Parse(GetValue(hana, "Port")),
            HanaUser = GetValue(hana, "User"),
            HanaPassword = GetValue(hana, "Password"),
            HanaSchema = GetValue(hana, "Schema")
        };
    }

    // 요청이 들어온 클라이언트 IP가 허용된 네트워크인지 확인한다.
    public bool IsAllowedClient(string? remoteIp)
    {
        // IP가 비어 있으면 거절한다.
        if (string.IsNullOrWhiteSpace(remoteIp))
        {
            return false;
        }

        // IP 문자열을 파싱한다.
        if (IPAddress.TryParse(remoteIp, out var address))
        {
            // Kestrel이 IPv4 클라이언트를 ::ffff:192.168.1.10 형태로 전달할 수 있다.
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            // 로컬호스트나 루프백은 항상 허용한다.
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            // 설정에 따라 사내망 허용 여부를 체크한다.
            var cfg = LoadConfig();
            if (!cfg.AllowPrivateNetwork)
            {
                return false;
            }

            // 쉼표나 세미콜론으로 구분된 CIDR 범위 중 하나에 들어오는지 확인한다.
            var allowedNetworks = cfg.AllowedNetworkPrefix.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var allowedNetwork in allowedNetworks)
            {
                if (TryParseCidr(allowedNetwork, address, out var isInRange) && isInRange)
                {
                    return true;
                }
            }

            _logger.LogWarning(
                "Client IP {RemoteIp} was denied. Allowed networks: {AllowedNetworks}",
                address,
                cfg.AllowedNetworkPrefix);
        }

        // 위 조건에 해당하지 않으면 거절한다.
        return false;
    }

    // SAP 사용자 마스터(OUSR/Service Layer Users)의 이메일로 승인 알림을 발송한다.
    public async Task SendApprovalEmailAsync(ApprovalNotificationRequest request)
    {
        ValidateApprovalRequestId(request.RequestId);
        var cfg = LoadConfig();
        if (string.IsNullOrWhiteSpace(request.ApproverEmail) && string.IsNullOrWhiteSpace(cfg.TestRecipient))
        {
            throw new InvalidOperationException(
                $"SAP user '{request.ApproverId}' has no email address in OUSR.E_Mail.");
        }
        var approverEmail = !string.IsNullOrWhiteSpace(cfg.TestRecipient)
            ? cfg.TestRecipient
            : request.ApproverEmail;
        var approvalLink = $"{cfg.BaseUrl.TrimEnd('/')}/approval/approve?requestId={Uri.EscapeDataString(request.RequestId)}";
        var rejectLink = $"{cfg.BaseUrl.TrimEnd('/')}/approval/reject?requestId={Uri.EscapeDataString(request.RequestId)}";
        var body = BuildApprovalEmailBody(request, approvalLink, rejectLink);

        var message = new MailMessage
        {
            From = new MailAddress(cfg.FromAddress),
            Subject = $"{(string.IsNullOrWhiteSpace(cfg.TestRecipient) ? string.Empty : "[TEST] ")}[SAP 승인 요청] {request.DocumentType} {request.DocumentNumber}",
            Body = body,
            IsBodyHtml = true
        };

        // 수신자 주소를 추가한다.
        message.To.Add(approverEmail);
        foreach (var ccAddress in ParseMailAddresses(cfg.CcAddress))
        {
            if (!string.Equals(ccAddress.Address, approverEmail, StringComparison.OrdinalIgnoreCase))
            {
                message.CC.Add(ccAddress);
            }
        }

        // SMTP 클라이언트를 생성한다.
        using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
        {
            Credentials = new NetworkCredential(cfg.SmtpUser, cfg.SmtpPassword),
            EnableSsl = cfg.MailEnableSsl
        };

        // 메일을 전송한다.
        await client.SendMailAsync(message);

        // 전송 성공 로그를 남긴다.
        _logger.LogInformation("Approval email sent for request {RequestId} to {Recipient}", request.RequestId, approverEmail);
    }

    // 자동 조회 건은 HANA OUSR에서 가져온 이메일만 사용하며 Service Layer Users 조회로 폴백하지 않는다.
    public async Task<bool> SendAutomatedApprovalEmailAsync(ApprovalNotificationRequest request, DraftDetail detail)
    {
        if (string.IsNullOrWhiteSpace(request.ApproverEmail))
        {
            _logger.LogWarning(
                "Approval email skipped for request {RequestId}: SAP user {ApproverId} has no OUSR.E_Mail address",
                request.RequestId,
                request.ApproverId);
            return false;
        }

        await SendApprovalEmailCoreAsync(request, request.ApproverEmail, detail);
        return true;
    }

    private async Task SendApprovalEmailCoreAsync(ApprovalNotificationRequest request, string recipient, DraftDetail detail)
    {
        ValidateApprovalRequestId(request.RequestId);
        var cfg = LoadConfig();
        var approverEmail = !string.IsNullOrWhiteSpace(cfg.TestRecipient) ? cfg.TestRecipient : recipient;
        var approvalLink = $"{cfg.BaseUrl.TrimEnd('/')}/approval/approve?requestId={Uri.EscapeDataString(request.RequestId)}";
        var rejectLink = $"{cfg.BaseUrl.TrimEnd('/')}/approval/reject?requestId={Uri.EscapeDataString(request.RequestId)}";
        var body = BuildApprovalEmailBody(request, approvalLink, rejectLink, detail);

        using var message = new MailMessage
        {
            From = new MailAddress(cfg.FromAddress),
            Subject = $"{(string.IsNullOrWhiteSpace(cfg.TestRecipient) ? string.Empty : "[TEST] ")}[SAP 승인 요청] {request.DocumentType} {request.DocumentNumber}",
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(approverEmail);
        foreach (var ccAddress in ParseMailAddresses(cfg.CcAddress))
        {
            if (!string.Equals(ccAddress.Address, approverEmail, StringComparison.OrdinalIgnoreCase))
            {
                message.CC.Add(ccAddress);
            }
        }

        using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
        {
            Credentials = new NetworkCredential(cfg.SmtpUser, cfg.SmtpPassword),
            EnableSsl = cfg.MailEnableSsl
        };
        await client.SendMailAsync(message);
        _logger.LogInformation("Automated approval email sent for request {RequestId} to {Recipient}", request.RequestId, approverEmail);
    }

    // SAP Service Layer로 승인/거절 처리 요청을 보낸다.
    public async Task<string> SubmitSapApprovalAsync(string requestId, string decision, string reason, string sapUser, string sapPassword)
    {
        var cfg = LoadConfig();

        // ApprovalRequests 엔터티를 새로 생성하는 대신 승인 서비스의 UpdateRequest 작업을 호출한다.
        var approvalJson = SapApprovalRequestFactory.CreateJson(requestId, decision, reason);
        return await ExecuteSapRequestAsync(
            HttpMethod.Post,
            "ApprovalRequestsService_UpdateRequest",
            approvalJson,
            sapUser,
            sapPassword);
    }

    // 인증과 세션 쿠키 처리를 공통화하여 이후 다른 Service Layer 기능에서도 재사용한다.
    public async Task<string> ExecuteSapRequestAsync(
        HttpMethod method,
        string relativePath,
        string? jsonBody,
        string sapUser,
        string sapPassword)
    {
        var cfg = LoadConfig();
        using var handler = CreateSapHandler();
        using var client = new HttpClient(handler);
        await GetSapSessionTokenAsync(cfg, sapUser, sapPassword, client);

        var request = new HttpRequestMessage(method, $"{cfg.ServiceLayerUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}");
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"SAP Service Layer request failed: {(int)response.StatusCode} {content}");
        }

        return await response.Content.ReadAsStringAsync();
    }

    public static string BuildApprovalEmailBody(
        ApprovalNotificationRequest request,
        string approvalLink,
        string rejectLink,
        DraftDetail? detail = null)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var detailRows = detail is null
            ? string.Empty
            : string.Join(string.Empty, detail.Lines.Select(line => $$"""
                <tr>
                  <td style="padding:9px 10px;border-bottom:1px solid #e4e7ec">{{E(line.ItemCode)}}</td>
                  <td style="padding:9px 10px;border-bottom:1px solid #e4e7ec;text-align:right">{{line.Quantity.ToString("N2", CultureInfo.InvariantCulture)}}</td>
                  <td style="padding:9px 10px;border-bottom:1px solid #e4e7ec;text-align:right">{{line.UnitPrice.ToString("N2", CultureInfo.InvariantCulture)}}</td>
                </tr>
                """));
        var detailSection = detail is null ? string.Empty : $$"""
            <h2 style="font-size:17px;margin:26px 0 12px">[전표 상세]</h2>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin-bottom:14px;background:#f8fafc;border-radius:8px;padding:14px;line-height:1.8">
              <tr><td style="color:#667085;width:120px">공급업체</td><td><strong>{{E(detail.SupplierCode)}}</strong></td></tr>
              <tr><td style="color:#667085">이름</td><td>{{E(detail.SupplierName)}}</td></tr>
            </table>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="border-collapse:collapse;border:1px solid #d0d5dd">
              <thead><tr style="background:#eaf2f8;color:#344054">
                <th align="left" style="padding:10px">품목번호</th>
                <th align="right" style="padding:10px">수량</th>
                <th align="right" style="padding:10px">단가 ({{E(detail.Currency)}})</th>
              </tr></thead>
              <tbody>{{detailRows}}</tbody>
              <tfoot><tr style="background:#f8fafc;font-weight:bold">
                <td colspan="2" align="right" style="padding:11px">총계</td>
                <td align="right" style="padding:11px">{{detail.DocumentTotal.ToString("N2", CultureInfo.InvariantCulture)}} {{E(detail.Currency)}}</td>
              </tr></tfoot>
            </table>
            """;
        return $$"""
            <!doctype html><html lang="ko"><body style="margin:0;background:#f4f6f9;font-family:Arial,'Malgun Gothic',sans-serif;color:#172033">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f4f6f9;padding:28px 12px"><tr><td align="center">
            <table role="presentation" width="600" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;background:#fff;border:1px solid #e4e7ec;border-radius:12px">
            <tr><td style="padding:36px">
            <p style="font-size:16px;margin:0 0 24px">안녕하세요 <strong>{{E(request.ApproverId)}}</strong>,</p>
            <p style="font-size:16px;line-height:1.7;margin:0 0 22px">아래의 SAP 승인 요청 건 확인 부탁 드립니다.</p>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f8fafc;border-radius:8px;padding:18px;line-height:1.9">
              <tr><td style="color:#667085;width:120px;padding:3px 0">전표유형</td><td><strong>{{E(request.DocumentType)}}</strong></td></tr>
              <tr><td style="color:#667085;padding:3px 0">전표번호</td><td>{{E(request.DocumentNumber)}}</td></tr>
              <tr><td style="color:#667085;padding:3px 0">초안키</td><td>{{E(request.DraftNumber)}}</td></tr>
              <tr><td style="color:#667085;padding:3px 0">생성자</td><td>{{E(request.Requester)}}</td></tr>
              <tr><td style="color:#667085;padding:3px 0">생성일</td><td>{{E(request.CreatedDate)}}</td></tr>
              <tr><td style="color:#667085;padding:3px 0">전표 총계</td><td>{{E(request.DocumentTotal)}}</td></tr>
              <tr><td style="color:#667085;padding:3px 0;vertical-align:top">초안비고</td><td style="white-space:pre-wrap">{{E(request.DraftRemarks)}}</td></tr>
            </table>
            {{detailSection}}
            <table role="presentation" cellspacing="0" cellpadding="0" style="margin:28px 0"><tr>
              <td style="padding-right:10px"><a href="{{E(approvalLink)}}" style="display:inline-block;background:#a8d8b9;color:#174d2a;text-decoration:none;font-weight:bold;padding:13px 25px;border:1px solid #86c89a;border-radius:8px">승인</a></td>
              <td><a href="{{E(rejectLink)}}" style="display:inline-block;background:#f3b6b6;color:#7f1d1d;text-decoration:none;font-weight:bold;padding:13px 25px;border:1px solid #e99a9a;border-radius:8px">거절</a></td>
            </tr></table>
            <p style="margin:0 0 8px">감사합니다.</p><p style="margin:0;color:#667085">티씬 SAP 승인 요청 알림</p>
            </td></tr></table></td></tr></table></body></html>
            """;
    }

    public static string ToSafeErrorMessage(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("SAP login failed", StringComparison.OrdinalIgnoreCase))
        {
            return "SAP ID 또는 암호를 확인해 주세요.";
        }
        return "SAP 요청을 처리하지 못했습니다. 잠시 후 다시 시도하거나 관리자에게 문의해 주세요.";
    }

    public static string ToAdminErrorMessage(Exception exception)
    {
        if (exception is ArgumentException argumentException && argumentException.ParamName == "requestId")
        {
            return "승인 요청 키 오류: requestId에는 SAP OWDD.WddCode의 양의 정수 값이 필요합니다.";
        }

        var raw = exception.Message;
        var status = System.Text.RegularExpressions.Regex.Match(raw, @"failed:\s*(\d{3})");
        var jsonStart = raw.IndexOf('{');
        if (jsonStart >= 0)
        {
            try
            {
                using var document = JsonDocument.Parse(raw[jsonStart..]);
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeNode) ? codeNode.ToString() : "unknown";
                    var message = "unknown";
                    if (error.TryGetProperty("message", out var messageNode))
                    {
                        message = messageNode.ValueKind == JsonValueKind.String
                            ? messageNode.GetString() ?? "unknown"
                            : messageNode.TryGetProperty("value", out var valueNode) ? valueNode.GetString() ?? "unknown" : messageNode.ToString();
                    }
                    return $"HTTP: {(status.Success ? status.Groups[1].Value : "unknown")} | SAP code: {code} | Message: {message}";
                }
            }
            catch (JsonException)
            {
            }
        }

        if (raw.Contains("SAP login failed", StringComparison.OrdinalIgnoreCase))
        {
            return $"HTTP: {(status.Success ? status.Groups[1].Value : "unknown")} | SAP 로그인 실패 (ID, 암호, 회사 DB 또는 권한 확인)";
        }

        return $"Type: {exception.GetType().Name} | Message: {SanitizeError(raw)}";
    }

    private static string SanitizeError(string value)
    {
        value = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)(password\s*[=:]\s*)[^,\s}]+", "$1***");
        return value.Length <= 800 ? value : value[..800] + "...";
    }

    private static void ValidateApprovalRequestId(string requestId)
    {
        if (!int.TryParse(requestId, out var code) || code <= 0)
        {
            throw new ArgumentException(
                "requestId must contain the positive numeric SAP approval request key (OWDD.WddCode).",
                nameof(requestId));
        }
    }

    private static bool TryGetString(JsonElement element, out string? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }
        }
        value = null;
        return false;
    }

    public static IReadOnlyList<MailAddress> ParseMailAddresses(string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return Array.Empty<MailAddress>();
        }

        return addresses
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => new MailAddress(x))
            .ToArray();
    }

    private static HttpClientHandler CreateSapHandler() => new()
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseCookies = true,
        CookieContainer = new CookieContainer()
    };

    // SAP 로그인 후 세션 토큰을 받기 위한 내부 메서드다.
    private async Task<string> GetSapSessionTokenAsync(ConfigData cfg, string sapUser, string sapPassword, HttpClient client)
    {
        // SAP 로그인 요청 본문을 만든다.
        var loginPayload = new
        {
            UserName = sapUser,
            Password = sapPassword,
            CompanyDB = cfg.CompanyDb
        };

        var loginJson = JsonSerializer.Serialize(loginPayload);
        _logger.LogInformation("SAP login user='{User}' passwordLength={Length} companyDb={CompanyDb}", sapUser, sapPassword?.Length ?? 0, cfg.CompanyDb);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{cfg.ServiceLayerUrl}/Login")
        {
            Content = new StringContent(loginJson, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"SAP login failed: {(int)response.StatusCode} {response.ReasonPhrase} {errorBody}");
        }

        return await response.Content.ReadAsStringAsync();
    }

    // CIDR 형식의 네트워크 범위를 파싱해 IP가 포함되는지 검사한다.
    private static bool TryParseCidr(string cidr, IPAddress address, out bool isInRange)
    {
        isInRange = false;
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        try
        {
            // CIDR을 네트워크 주소와 prefix 길이로 분리한다.
            var parts = cidr.Split('/', 2);
            if (parts.Length != 2 || !byte.TryParse(parts[1], out var prefixLength))
            {
                return false;
            }

            // 네트워크 주소와 현재 IP 주소를 바이트로 변환한다.
            var networkAddress = IPAddress.Parse(parts[0]);
            var ipBytes = address.GetAddressBytes();
            var networkBytes = networkAddress.GetAddressBytes();

            if (ipBytes.Length != networkBytes.Length)
            {
                return false;
            }

            // 서브넷 마스크를 계산한다.
            var mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
            var addressValue = BitConverter.ToUInt32(ipBytes.Reverse().ToArray(), 0);
            var networkValue = BitConverter.ToUInt32(networkBytes.Reverse().ToArray(), 0);
            var maskValue = mask;

            // IP가 네트워크 범위 안에 있는지 계산한다.
            isInRange = (addressValue & maskValue) == (networkValue & maskValue);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // XML 노드에서 문자열 값을 읽어온다.
    private static string GetValue(XmlElement parent, string name)
    {
        var node = parent[name];
        return node?.InnerText ?? string.Empty;
    }

}

// SAP ApprovalRequestsService_UpdateRequest가 요구하는 URL 본문을 생성한다.
public static class SapApprovalRequestFactory
{
    public static string CreateJson(string requestId, string decision, string? reason)
    {
        if (!int.TryParse(requestId, out var code) || code <= 0)
        {
            throw new ArgumentException("requestId must be a positive integer.", nameof(requestId));
        }

        var status = decision.Trim().ToLowerInvariant() switch
        {
            "approve" => "ardApproved",
            "reject" => "ardNotApproved",
            _ => throw new ArgumentException("decision must be either 'approve' or 'reject'.", nameof(decision))
        };

        var payload = new
        {
            ApprovalRequest = new
            {
                Code = code,
                ApprovalRequestDecisions = new[]
                {
                    new
                    {
                        Status = status,
                        Remarks = reason ?? string.Empty
                    }
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }
}

// XML 설정값을 담는 데이터 모델이다.
public class ConfigData
{
    public string BaseUrl { get; set; } = string.Empty;
    public string AllowedNetworkPrefix { get; set; } = string.Empty;
    public bool AllowPrivateNetwork { get; set; }
    public bool EnableSsl { get; set; }
    public string ServiceLayerUrl { get; set; } = string.Empty;
    public string CompanyDb { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int SessionTimeoutSeconds { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; }
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public bool MailEnableSsl { get; set; }
    public string TestRecipient { get; set; } = string.Empty;
    public string CcAddress { get; set; } = string.Empty;
    public bool ReminderEnabled { get; set; }
    public string[] ReminderTimes { get; set; } = Array.Empty<string>();
    public string ReminderSubject { get; set; } = string.Empty;
    public string ReminderBody { get; set; } = string.Empty;
    public string HanaHost { get; set; } = string.Empty;
    public int HanaPort { get; set; }
    public string HanaUser { get; set; } = string.Empty;
    public string HanaPassword { get; set; } = string.Empty;
    public string HanaSchema { get; set; } = string.Empty;
}
