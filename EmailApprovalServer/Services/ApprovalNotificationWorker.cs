namespace EmailApprovalServer.Services;

public sealed class ApprovalNotificationWorker : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);
    private readonly HanaApprovalRepository _repository;
    private readonly ApprovalService _approvalService;
    private readonly ILogger<ApprovalNotificationWorker> _logger;
    private readonly HashSet<string> _completedReminderSlots = new();
    private readonly HashSet<int> _sentButUnmarkedDrafts = new();
    private DateTimeOffset _nextScan = DateTimeOffset.MinValue;

    public ApprovalNotificationWorker(
        HanaApprovalRepository repository,
        ApprovalService approvalService,
        ILogger<ApprovalNotificationWorker> logger)
    {
        _repository = repository;
        _approvalService = approvalService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            try
            {
                if (now >= _nextScan)
                {
                    await SendNewApprovalNotificationsAsync(stoppingToken);
                    _nextScan = now.Add(ScanInterval);
                }

                var cfg = _approvalService.LoadConfig();
                if (cfg.ReminderEnabled && TryGetDueReminderSlot(now, cfg.ReminderTimes, out var slotKey) &&
                    _completedReminderSlots.Add(slotKey))
                {
                    await SendRemindersAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Approval notification cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task SendNewApprovalNotificationsAsync(CancellationToken cancellationToken)
    {
        foreach (var draftEntry in _sentButUnmarkedDrafts.ToArray())
        {
            try
            {
                await _repository.MarkDraftNotifiedAsync(draftEntry, cancellationToken);
                _sentButUnmarkedDrafts.Remove(draftEntry);
                _logger.LogInformation("Marked previously sent ODRF DraftEntry {DraftEntry} U_EMAILNOTI=Y", draftEntry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retry to mark ODRF DraftEntry {DraftEntry} failed", draftEntry);
            }
        }

        var pending = await _repository.GetPendingAsync(true, cancellationToken);
        foreach (var draftGroup in pending
                     .Where(x => !_sentButUnmarkedDrafts.Contains(x.DraftEntry))
                     .GroupBy(x => x.DraftEntry))
        {
            var allSent = true;
            DraftDetail detail;
            try
            {
                detail = await _repository.GetDraftDetailAsync(draftGroup.Key, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load attachment data for DraftEntry {DraftEntry}", draftGroup.Key);
                continue;
            }
            foreach (var approval in draftGroup)
            {
                try
                {
                    await _approvalService.SendAutomatedApprovalEmailAsync(approval.ToNotification(), detail);
                }
                catch (Exception ex)
                {
                    allSent = false;
                    _logger.LogError(ex, "Initial approval email failed for WddCode {RequestId}, approver {ApproverId}", approval.RequestId, approval.ApproverId);
                }
            }

            if (allSent)
            {
                try
                {
                    await _repository.MarkDraftNotifiedAsync(draftGroup.Key, cancellationToken);
                    _logger.LogInformation("Marked ODRF DraftEntry {DraftEntry} U_EMAILNOTI=Y", draftGroup.Key);
                }
                catch (Exception ex)
                {
                    _sentButUnmarkedDrafts.Add(draftGroup.Key);
                    _logger.LogError(ex, "Failed to mark ODRF DraftEntry {DraftEntry} U_EMAILNOTI=Y; it will be retried", draftGroup.Key);
                }
            }
        }
    }

    public async Task SendRemindersAsync(CancellationToken cancellationToken)
    {
        var pending = await _repository.GetPendingAsync(false, cancellationToken);
        foreach (var draftGroup in pending.GroupBy(x => x.DraftEntry))
        {
            DraftDetail detail;
            try
            {
                detail = await _repository.GetDraftDetailAsync(draftGroup.Key, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load reminder attachment for DraftEntry {DraftEntry}", draftGroup.Key);
                continue;
            }
            foreach (var approval in draftGroup)
            {
                try
                {
                    await _approvalService.SendAutomatedApprovalEmailAsync(approval.ToNotification(), detail);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reminder email failed for WddCode {RequestId}, approver {ApproverId}", approval.RequestId, approval.ApproverId);
                }
            }
        }
    }

    public static bool TryGetDueReminderSlot(DateTimeOffset now, IEnumerable<string> configuredTimes, out string slotKey)
    {
        slotKey = string.Empty;
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        foreach (var configuredTime in configuredTimes)
        {
            if (TimeOnly.TryParse(configuredTime, out var time) && now.Hour == time.Hour && now.Minute == time.Minute)
            {
                slotKey = $"{now:yyyy-MM-dd}:{time:HH:mm}";
                return true;
            }
        }
        return false;
    }
}
