using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Persistence.Repositories;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Strings;

namespace Umbraco.Cms.Core.Services;

public class ElementService : PublishableContentServiceBase<IElement>, IElementService
{
    private readonly IElementRepository _elementRepository;
    private readonly ILogger<ElementService> _logger;
    private readonly IShortStringHelper _shortStringHelper;

    public ElementService(
        ICoreScopeProvider provider,
        ILoggerFactory loggerFactory,
        IEventMessagesFactory eventMessagesFactory,
        IAuditRepository auditRepository,
        IContentTypeRepository contentTypeRepository,
        IElementRepository elementRepository,
        ILanguageRepository languageRepository,
        Lazy<IPropertyValidationService> propertyValidationService,
        ICultureImpactFactory cultureImpactFactory,
        PropertyEditorCollection propertyEditorCollection,
        IIdKeyMap idKeyMap,
        IShortStringHelper shortStringHelper)
        : base(
            provider,
            loggerFactory,
            eventMessagesFactory,
            auditRepository,
            contentTypeRepository,
            elementRepository,
            languageRepository,
            propertyValidationService,
            cultureImpactFactory,
            propertyEditorCollection,
            idKeyMap)
    {
        _elementRepository = elementRepository;
        _shortStringHelper = shortStringHelper;
        _logger = loggerFactory.CreateLogger<ElementService>();
    }

    #region Create

    public IElement Create(string name, string contentTypeAlias, int userId = Constants.Security.SuperUserId)
    {
        IContentType contentType = GetContentType(contentTypeAlias)
                                   // causes rollback
                                   ?? throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias));

        var element = new Element(name, contentType, userId);

        return element;
    }

    #endregion

    #region Others

    // TODO ELEMENTS: create abstractions of the implementations in this region, and share them with ContentService

    Attempt<OperationResult?> IContentServiceBase<IElement>.Save(IEnumerable<IElement> contents, int userId) =>
        Attempt.Succeed(Save(contents, userId));

    public ContentDataIntegrityReport CheckDataIntegrity(ContentDataIntegrityReportOptions options)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            ContentDataIntegrityReport report = _elementRepository.CheckDataIntegrity(options);

            if (report.FixedIssues.Count > 0)
            {
                // The event args needs a content item so we'll make a fake one with enough properties to not cause a null ref
                var root = new Element("root", -1, new ContentType(_shortStringHelper, -1)) { Id = -1, Key = Guid.Empty };
                scope.Notifications.Publish(new ElementTreeChangeNotification(root, TreeChangeTypes.RefreshAll, EventMessagesFactory.Get()));
            }

            scope.Complete();

            return report;
        }
    }

    #endregion

    #region Abstract implementations

    protected override IElement CreateContentInstance(string name, int parentId, IContentType contentType, int userId)
        => new Element(name, contentType, userId);

    // TODO ELEMENTS: this should only be on the content service
    protected override IElement CreateContentInstance(string name, IElement parent, IContentType contentType, int userId)
        => throw new InvalidOperationException("Elements cannot be nested underneath one another");

    protected override UmbracoObjectTypes ContentObjectType
        => UmbracoObjectTypes.Element;

    // TODO ELEMENTS: implement publishing
    protected override PublishResult CommitDocumentChanges(ICoreScope scope, IElement content, EventMessages eventMessages, IReadOnlyCollection<ILanguage> allLangs, IDictionary<string, object?>? notificationState, int userId)
        => throw new NotImplementedException();

    protected override void DeleteLocked(ICoreScope scope, IElement content, EventMessages evtMsgs)
    {
        _elementRepository.Delete(content);
        scope.Notifications.Publish(new ElementDeletedNotification(content, evtMsgs));
    }

    protected override ILogger<ElementService> Logger => _logger;

    protected override int[] ReadLockIds => WriteLockIds;

    protected override int[] WriteLockIds => new[] { Constants.Locks.ElementTree };

    protected override SavingNotification<IElement> SavingNotification(IElement content, EventMessages eventMessages)
        => new ElementSavingNotification(content, eventMessages);

    protected override SavedNotification<IElement> SavedNotification(IElement content, EventMessages eventMessages)
        => new ElementSavedNotification(content, eventMessages);

    protected override SavingNotification<IElement> SavingNotification(IEnumerable<IElement> content, EventMessages eventMessages)
        => new ElementSavingNotification(content, eventMessages);

    protected override SavedNotification<IElement> SavedNotification(IEnumerable<IElement> content, EventMessages eventMessages)
        => new ElementSavedNotification(content, eventMessages);

    protected override TreeChangeNotification<IElement> TreeChangeNotification(IElement content, TreeChangeTypes changeTypes, EventMessages eventMessages)
        => new ElementTreeChangeNotification(content, changeTypes, eventMessages);

    protected override TreeChangeNotification<IElement> TreeChangeNotification(IEnumerable<IElement> content, TreeChangeTypes changeTypes, EventMessages eventMessages)
        => new ElementTreeChangeNotification(content, changeTypes, eventMessages);

    protected override DeletingNotification<IElement> DeletingNotification(IElement content, EventMessages eventMessages)
        => new ElementDeletingNotification(content, eventMessages);

    protected override IStatefulNotification UnpublishedNotification(IElement content, EventMessages eventMessages)
        => new ElementUnpublishedNotification(content, eventMessages);

    protected override DeletingVersionsNotification<IElement> DeletingVersionsNotification(int id, EventMessages messages, int specificVersion = default, bool deletePriorVersions = false, DateTime dateToRetain = default)
        => new ElementDeletingVersionsNotification(id, messages, specificVersion, deletePriorVersions, dateToRetain);

    protected override DeletedVersionsNotification<IElement> DeletedVersionsNotification(int id, EventMessages messages, int specificVersion = default, bool deletePriorVersions = false, DateTime dateToRetain = default)
        => new ElementDeletedVersionsNotification(id, messages, specificVersion, deletePriorVersions, dateToRetain);

    protected override CancelableEnumerableObjectNotification<IElement> PublishingNotification(IElement content, EventMessages eventMessages)
        => new ElementPublishingNotification(content, eventMessages);

    #endregion
}
