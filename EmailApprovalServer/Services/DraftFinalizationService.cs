namespace EmailApprovalServer.Services;

public sealed class DraftFinalizationService
{
    private readonly HanaApprovalRepository _repository;
    private readonly ApprovalService _approvalService;
    private readonly ILogger<DraftFinalizationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DraftFinalizationService(
        HanaApprovalRepository repository,
        ApprovalService approvalService,
        ILogger<DraftFinalizationService> logger)
    {
        _repository = repository;
        _approvalService = approvalService;
        _logger = logger;
    }

    public async Task<bool> FinalizeRequestIfApprovedAsync(
        int requestId,
        string sapUser,
        string sapPassword,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var drafts = await _repository.GetApprovedDraftsAsync(requestId, cancellationToken);
            foreach (var draft in drafts)
            {
                await FinalizeAsync(draft, sapUser, sapPassword);
            }
            return drafts.Count > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FinalizeAllApprovedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cfg = _approvalService.LoadConfig();
            var drafts = await _repository.GetApprovedDraftsAsync(null, cancellationToken);
            _logger.LogInformation(
                "Approved draft finalization scan found {DraftCount} ungenerated draft(s)",
                drafts.Count);
            foreach (var draft in drafts)
            {
                try
                {
                    await FinalizeAsync(draft, cfg.UserName, cfg.Password);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Approved DraftEntry {DraftEntry} could not be saved as a regular document; it will be retried",
                        draft.DraftEntry);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FinalizeAsync(ApprovedDraft draft, string sapUser, string sapPassword)
    {
        await _approvalService.SaveDraftToDocumentAsync(draft.DraftEntry, sapUser, sapPassword);
        _logger.LogInformation(
            "Approved DraftEntry {DraftEntry} (WddCode {RequestId}, ObjType {ObjectType}) was saved as a regular document",
            draft.DraftEntry,
            draft.RequestId,
            draft.ObjectType);
    }
}
