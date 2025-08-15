using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentPublishing;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace Umbraco.Cms.Core.Services;

internal sealed class ContentPublishingService : ContentPublishingServiceBase<IContent, IContentService>, IContentPublishingService
{
    private const string PublishBranchOperationType = "ContentPublishBranch";

    private readonly ICoreScopeProvider _coreScopeProvider;
    private readonly IContentService _contentService;
    private readonly IUserIdKeyResolver _userIdKeyResolver;
    private readonly ILogger<ContentPublishingService> _logger;
    private readonly ILongRunningOperationService _longRunningOperationService;

    public ContentPublishingService(
        ICoreScopeProvider coreScopeProvider,
        IContentService contentService,
        IUserIdKeyResolver userIdKeyResolver,
        IContentValidationService contentValidationService,
        IContentTypeService contentTypeService,
        ILanguageService languageService,
        IOptionsMonitor<ContentSettings> optionsMonitor,
        IRelationService relationService,
        ILogger<ContentPublishingService> logger,
        ILongRunningOperationService longRunningOperationService)
        : base(
            coreScopeProvider,
            contentService,
            userIdKeyResolver,
            contentValidationService,
            contentTypeService,
            languageService,
            optionsMonitor,
            relationService,
            logger)
    {
        _coreScopeProvider = coreScopeProvider;
        _contentService = contentService;
        _userIdKeyResolver = userIdKeyResolver;
        _logger = logger;
        _longRunningOperationService = longRunningOperationService;
    }

    /// <inheritdoc />
    [Obsolete("This method is not longer used as the 'force' parameter has been extended into options for publishing unpublished and re-publishing changed content. Please use the overload containing the parameter for those options instead. Scheduled for removal in Umbraco 17.")]
    public async Task<Attempt<ContentPublishingBranchResult, ContentPublishingOperationStatus>> PublishBranchAsync(Guid key, IEnumerable<string> cultures, bool force, Guid userKey)
        => await PublishBranchAsync(key, cultures, force ? PublishBranchFilter.IncludeUnpublished : PublishBranchFilter.Default, userKey);

    /// <inheritdoc />
    [Obsolete("Please use the overload containing all parameters. Scheduled for removal in Umbraco 17.")]
    public async Task<Attempt<ContentPublishingBranchResult, ContentPublishingOperationStatus>> PublishBranchAsync(Guid key, IEnumerable<string> cultures, PublishBranchFilter publishBranchFilter, Guid userKey)
        => await PublishBranchAsync(key, cultures, publishBranchFilter, userKey, false);

    /// <inheritdoc />
    public async Task<Attempt<ContentPublishingBranchResult, ContentPublishingOperationStatus>> PublishBranchAsync(
        Guid key,
        IEnumerable<string> cultures,
        PublishBranchFilter publishBranchFilter,
        Guid userKey,
        bool useBackgroundThread)
    {
        if (useBackgroundThread is false)
        {
            Attempt<ContentPublishingBranchInternalResult, ContentPublishingOperationStatus> minimalAttempt
                = await PerformPublishBranchAsync(key, cultures, publishBranchFilter, userKey, returnContent: true);
            return MapInternalPublishingAttempt(minimalAttempt);
        }

        _logger.LogInformation("Starting async background thread for publishing branch.");
        Attempt<Guid, LongRunningOperationEnqueueStatus> enqueueAttempt = await _longRunningOperationService.RunAsync(
            PublishBranchOperationType,
            async _ => await PerformPublishBranchAsync(key, cultures, publishBranchFilter, userKey, returnContent: false),
            allowConcurrentExecution: true);
        if (enqueueAttempt.Success)
        {
            return Attempt.SucceedWithStatus(
                ContentPublishingOperationStatus.Accepted,
                new ContentPublishingBranchResult { AcceptedTaskId = enqueueAttempt.Result });
        }

        return Attempt.FailWithStatus(
            ContentPublishingOperationStatus.Unknown,
            new ContentPublishingBranchResult
            {
                FailedItems =
                [
                    new ContentPublishingBranchItemResult
                    {
                        Key = key,
                        OperationStatus = ContentPublishingOperationStatus.Unknown,
                    }
                ],
            });
    }

    private async Task<Attempt<ContentPublishingBranchInternalResult, ContentPublishingOperationStatus>> PerformPublishBranchAsync(
        Guid key,
        IEnumerable<string> cultures,
        PublishBranchFilter publishBranchFilter,
        Guid userKey,
        bool returnContent)
    {
        using ICoreScope scope = _coreScopeProvider.CreateCoreScope();
        IContent? content = _contentService.GetById(key);
        if (content is null)
        {
            return Attempt.FailWithStatus(
                ContentPublishingOperationStatus.ContentNotFound,
                new ContentPublishingBranchInternalResult
                {
                    FailedItems =
                    [
                        new ContentPublishingBranchItemResult
                        {
                            Key = key,
                            OperationStatus = ContentPublishingOperationStatus.ContentNotFound,
                        }
                    ],
                });
        }

        var userId = await _userIdKeyResolver.GetAsync(userKey);
        IEnumerable<PublishResult> result = _contentService.PublishBranch(content, publishBranchFilter, cultures.ToArray(), userId);
        scope.Complete();

        var itemResults = result.ToDictionary(r => r.Content.Key, ToContentPublishingOperationStatus);
        var branchResult = new ContentPublishingBranchInternalResult
        {
            ContentKey = content.Key,
            Content = returnContent ? content : null,
            SucceededItems = itemResults
                .Where(i => i.Value is ContentPublishingOperationStatus.Success)
                .Select(i => new ContentPublishingBranchItemResult { Key = i.Key, OperationStatus = i.Value })
                .ToArray(),
            FailedItems = itemResults
                .Where(i => i.Value is not ContentPublishingOperationStatus.Success)
                .Select(i => new ContentPublishingBranchItemResult { Key = i.Key, OperationStatus = i.Value })
                .ToArray(),
        };

        Attempt<ContentPublishingBranchInternalResult, ContentPublishingOperationStatus> attempt = branchResult.FailedItems.Any() is false
            ? Attempt.SucceedWithStatus(ContentPublishingOperationStatus.Success, branchResult)
            : Attempt.FailWithStatus(ContentPublishingOperationStatus.FailedBranch, branchResult);

        return attempt;
    }

    /// <inheritdoc/>
    public async Task<bool> IsPublishingBranchAsync(Guid taskId)
        => await _longRunningOperationService.GetStatusAsync(taskId) is LongRunningOperationStatus.Enqueued or LongRunningOperationStatus.Running;

    /// <inheritdoc/>
    public async Task<Attempt<ContentPublishingBranchResult, ContentPublishingOperationStatus>> GetPublishBranchResultAsync(Guid taskId)
    {
        Attempt<Attempt<ContentPublishingBranchInternalResult, ContentPublishingOperationStatus>, LongRunningOperationResultStatus> result =
            await _longRunningOperationService
                .GetResultAsync<Attempt<ContentPublishingBranchInternalResult, ContentPublishingOperationStatus>>(taskId);

        if (result.Success is false)
        {
            return Attempt.FailWithStatus(
                result.Status switch
                {
                    LongRunningOperationResultStatus.OperationNotFound => ContentPublishingOperationStatus.TaskResultNotFound,
                    LongRunningOperationResultStatus.OperationFailed => ContentPublishingOperationStatus.Failed,
                    _ => ContentPublishingOperationStatus.Unknown,
                },
                new ContentPublishingBranchResult());
        }

        return MapInternalPublishingAttempt(result.Result);
    }

    private Attempt<ContentPublishingBranchResult, ContentPublishingOperationStatus> MapInternalPublishingAttempt(
        Attempt<ContentPublishingBranchInternalResult, ContentPublishingOperationStatus> minimalAttempt) =>
        minimalAttempt.Success
            ? Attempt.SucceedWithStatus(minimalAttempt.Status, MapMinimalPublishingBranchResult(minimalAttempt.Result))
            : Attempt.FailWithStatus(minimalAttempt.Status, MapMinimalPublishingBranchResult(minimalAttempt.Result));

    private ContentPublishingBranchResult MapMinimalPublishingBranchResult(ContentPublishingBranchInternalResult internalResult) =>
        new()
        {
            Content = internalResult.Content
                      ?? (internalResult.ContentKey is null ? null : _contentService.GetById(internalResult.ContentKey.Value)),
            SucceededItems = internalResult.SucceededItems,
            FailedItems = internalResult.FailedItems,
        };
}
