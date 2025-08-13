using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Persistence;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Persistence.Repositories;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

namespace Umbraco.Cms.Core.Services;

/// <summary>
///     Implements the content service.
/// </summary>
public class ContentService : PublishableContentServiceBase<IContent>, IContentService
{
    private readonly IDocumentBlueprintRepository _documentBlueprintRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IEntityRepository _entityRepository;
    private readonly ILanguageRepository _languageRepository;
    private readonly ILogger<ContentService> _logger;
    private readonly Lazy<IPropertyValidationService> _propertyValidationService;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly ICultureImpactFactory _cultureImpactFactory;
    private readonly IUserIdKeyResolver _userIdKeyResolver;
    private readonly PropertyEditorCollection _propertyEditorCollection;
    private readonly IIdKeyMap _idKeyMap;
    private ContentSettings _contentSettings;
    private readonly IRelationService _relationService;
    private IQuery<IContent>? _queryNotTrashed;

    #region Constructors

    public ContentService(
        ICoreScopeProvider provider,
        ILoggerFactory loggerFactory,
        IEventMessagesFactory eventMessagesFactory,
        IDocumentRepository documentRepository,
        IEntityRepository entityRepository,
        IAuditRepository auditRepository,
        IContentTypeRepository contentTypeRepository,
        IDocumentBlueprintRepository documentBlueprintRepository,
        ILanguageRepository languageRepository,
        Lazy<IPropertyValidationService> propertyValidationService,
        IShortStringHelper shortStringHelper,
        ICultureImpactFactory cultureImpactFactory,
        IUserIdKeyResolver userIdKeyResolver,
        PropertyEditorCollection propertyEditorCollection,
        IIdKeyMap idKeyMap,
        IOptionsMonitor<ContentSettings> optionsMonitor,
        IRelationService relationService)
        : base(
            provider,
            loggerFactory,
            eventMessagesFactory,
            auditRepository,
            contentTypeRepository,
            documentRepository,
            languageRepository,
            propertyValidationService,
            cultureImpactFactory,
            propertyEditorCollection,
            idKeyMap)
    {
        _documentRepository = documentRepository;
        _entityRepository = entityRepository;
        _documentBlueprintRepository = documentBlueprintRepository;
        _languageRepository = languageRepository;
        _propertyValidationService = propertyValidationService;
        _shortStringHelper = shortStringHelper;
        _cultureImpactFactory = cultureImpactFactory;
        _userIdKeyResolver = userIdKeyResolver;
        _propertyEditorCollection = propertyEditorCollection;
        _idKeyMap = idKeyMap;
        _contentSettings = optionsMonitor.CurrentValue;
        optionsMonitor.OnChange((contentSettings) =>
        {
            _contentSettings = contentSettings;
        });
        _relationService = relationService;
        _logger = loggerFactory.CreateLogger<ContentService>();
    }

    [Obsolete("Use non-obsolete constructor. Scheduled for removal in V17.")]

    public ContentService(
        ICoreScopeProvider provider,
        ILoggerFactory loggerFactory,
        IEventMessagesFactory eventMessagesFactory,
        IDocumentRepository documentRepository,
        IEntityRepository entityRepository,
        IAuditRepository auditRepository,
        IContentTypeRepository contentTypeRepository,
        IDocumentBlueprintRepository documentBlueprintRepository,
        ILanguageRepository languageRepository,
        Lazy<IPropertyValidationService> propertyValidationService,
        IShortStringHelper shortStringHelper,
        ICultureImpactFactory cultureImpactFactory,
        IUserIdKeyResolver userIdKeyResolver,
        PropertyEditorCollection propertyEditorCollection,
        IIdKeyMap idKeyMap)
        : this(
            provider,
            loggerFactory,
            eventMessagesFactory,
            documentRepository,
            entityRepository,
            auditRepository,
            contentTypeRepository,
            documentBlueprintRepository,
            languageRepository,
            propertyValidationService,
            shortStringHelper,
            cultureImpactFactory,
            userIdKeyResolver,
            propertyEditorCollection,
            idKeyMap,
            StaticServiceProvider.Instance.GetRequiredService<IOptionsMonitor<ContentSettings>>(),
            StaticServiceProvider.Instance.GetRequiredService<IRelationService>())
    {
    }
    [Obsolete("Use non-obsolete constructor. Scheduled for removal in V17.")]
    public ContentService(
        ICoreScopeProvider provider,
        ILoggerFactory loggerFactory,
        IEventMessagesFactory eventMessagesFactory,
        IDocumentRepository documentRepository,
        IEntityRepository entityRepository,
        IAuditRepository auditRepository,
        IContentTypeRepository contentTypeRepository,
        IDocumentBlueprintRepository documentBlueprintRepository,
        ILanguageRepository languageRepository,
        Lazy<IPropertyValidationService> propertyValidationService,
        IShortStringHelper shortStringHelper,
        ICultureImpactFactory cultureImpactFactory,
        IUserIdKeyResolver userIdKeyResolver,
        PropertyEditorCollection propertyEditorCollection)
        : this(
            provider,
            loggerFactory,
            eventMessagesFactory,
            documentRepository,
            entityRepository,
            auditRepository,
            contentTypeRepository,
            documentBlueprintRepository,
            languageRepository,
            propertyValidationService,
            shortStringHelper,
            cultureImpactFactory,
            userIdKeyResolver,
            propertyEditorCollection,
            StaticServiceProvider.Instance.GetRequiredService<IIdKeyMap>())
    {
    }

    #endregion

    #region Static queries

    // lazy-constructed because when the ctor runs, the query factory may not be ready
    private IQuery<IContent> QueryNotTrashed =>
        _queryNotTrashed ??= Query<IContent>().Where(x => x.Trashed == false);

    #endregion

    #region Rollback

    public OperationResult Rollback(int id, int versionId, string culture = "*", int userId = Constants.Security.SuperUserId)
    {
        EventMessages evtMsgs = EventMessagesFactory.Get();

        // Get the current copy of the node
        IContent? content = GetById(id);

        // Get the version
        IContent? version = GetVersion(versionId);

        // Good old null checks
        if (content == null || version == null || content.Trashed)
        {
            return new OperationResult(OperationResultType.FailedCannot, evtMsgs);
        }

        // Store the result of doing the save of content for the rollback
        OperationResult rollbackSaveResult;

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            var rollingBackNotification = new ContentRollingBackNotification(content, evtMsgs);
            if (scope.Notifications.PublishCancelable(rollingBackNotification))
            {
                scope.Complete();
                return OperationResult.Cancel(evtMsgs);
            }

            // Copy the changes from the version
            content.CopyFrom(version, culture);

            // Save the content for the rollback
            rollbackSaveResult = Save(content, userId);

            // Depending on the save result - is what we log & audit along with what we return
            if (rollbackSaveResult.Success == false)
            {
                // Log the error/warning
                _logger.LogError(
                    "User '{UserId}' was unable to rollback content '{ContentId}' to version '{VersionId}'", userId, id, versionId);
            }
            else
            {
                scope.Notifications.Publish(
                    new ContentRolledBackNotification(content, evtMsgs).WithStateFrom(rollingBackNotification));

                // Logging & Audit message
                _logger.LogInformation("User '{UserId}' rolled back content '{ContentId}' to version '{VersionId}'", userId, id, versionId);
                Audit(AuditType.RollBack, userId, id, $"Content '{content.Name}' was rolled back to version '{versionId}'");
            }

            scope.Complete();
        }

        return rollbackSaveResult;
    }

    #endregion

    #region Permissions

    /// <summary>
    ///     Used to bulk update the permissions set for a content item. This will replace all permissions
    ///     assigned to an entity with a list of user id &amp; permission pairs.
    /// </summary>
    /// <param name="permissionSet"></param>
    public void SetPermissions(EntityPermissionSet permissionSet)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);
            _documentRepository.ReplaceContentPermissions(permissionSet);
            scope.Complete();
        }
    }

    /// <summary>
    ///     Assigns a single permission to the current content item for the specified group ids
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="permission"></param>
    /// <param name="groupIds"></param>
    public void SetPermission(IContent entity, string permission, IEnumerable<int> groupIds)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);
            _documentRepository.AssignEntityPermission(entity, permission, groupIds);
            scope.Complete();
        }
    }

    /// <summary>
    ///     Returns implicit/inherited permissions assigned to the content item for all user groups
    /// </summary>
    /// <param name="content"></param>
    /// <returns></returns>
    public EntityPermissionCollection GetPermissions(IContent content)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            return _documentRepository.GetPermissionsForEntity(content.Id);
        }
    }

    #endregion

    #region Create

    /// <summary>
    ///     Creates an <see cref="IContent" /> object using the alias of the <see cref="IContentType" />
    ///     that this Content should based on.
    /// </summary>
    /// <remarks>
    ///     Note that using this method will simply return a new IContent without any identity
    ///     as it has not yet been persisted. It is intended as a shortcut to creating new content objects
    ///     that does not invoke a save operation against the database.
    /// </remarks>
    /// <param name="name">Name of the Content object</param>
    /// <param name="parentId">Id of Parent for the new Content</param>
    /// <param name="contentTypeAlias">Alias of the <see cref="IContentType" /></param>
    /// <param name="userId">Optional id of the user creating the content</param>
    /// <returns>
    ///     <see cref="IContent" />
    /// </returns>
    public IContent Create(string name, Guid parentId, string contentTypeAlias, int userId = Constants.Security.SuperUserId)
    {
        // TODO: what about culture?
        IContent? parent = GetById(parentId);
        return Create(name, parent, contentTypeAlias, userId);
    }

    /// <summary>
    ///     Creates an <see cref="IContent" /> object of a specified content type.
    /// </summary>
    /// <remarks>
    ///     This method simply returns a new, non-persisted, IContent without any identity. It
    ///     is intended as a shortcut to creating new content objects that does not invoke a save
    ///     operation against the database.
    /// </remarks>
    /// <param name="name">The name of the content object.</param>
    /// <param name="parentId">The identifier of the parent, or -1.</param>
    /// <param name="contentTypeAlias">The alias of the content type.</param>
    /// <param name="userId">The optional id of the user creating the content.</param>
    /// <returns>The content object.</returns>
    public IContent Create(string name, int parentId, string contentTypeAlias, int userId = Constants.Security.SuperUserId)
    {
        // TODO: what about culture?
        IContentType contentType = GetContentType(contentTypeAlias);
        return Create(name, parentId, contentType, userId);
    }

    /// <summary>
    ///     Creates an <see cref="IContent" /> object of a specified content type.
    /// </summary>
    /// <remarks>
    ///     This method simply returns a new, non-persisted, IContent without any identity. It
    ///     is intended as a shortcut to creating new content objects that does not invoke a save
    ///     operation against the database.
    /// </remarks>
    /// <param name="name">The name of the content object.</param>
    /// <param name="parentId">The identifier of the parent, or -1.</param>
    /// <param name="contentType">The content type of the content</param>
    /// <param name="userId">The optional id of the user creating the content.</param>
    /// <returns>The content object.</returns>
    public IContent Create(string name, int parentId, IContentType contentType, int userId = Constants.Security.SuperUserId)
    {
        if (contentType is null)
        {
            throw new ArgumentException("Content type must be specified", nameof(contentType));
        }

        IContent? parent = parentId > 0 ? GetById(parentId) : null;
        if (parentId > 0 && parent is null)
        {
            throw new ArgumentException("No content with that id.", nameof(parentId));
        }

        var content = new Content(name, parentId, contentType, userId);

        return content;
    }

    /// <summary>
    ///     Creates an <see cref="IContent" /> object of a specified content type, under a parent.
    /// </summary>
    /// <remarks>
    ///     This method simply returns a new, non-persisted, IContent without any identity. It
    ///     is intended as a shortcut to creating new content objects that does not invoke a save
    ///     operation against the database.
    /// </remarks>
    /// <param name="name">The name of the content object.</param>
    /// <param name="parent">The parent content object.</param>
    /// <param name="contentTypeAlias">The alias of the content type.</param>
    /// <param name="userId">The optional id of the user creating the content.</param>
    /// <returns>The content object.</returns>
    public IContent Create(string name, IContent? parent, string contentTypeAlias, int userId = Constants.Security.SuperUserId)
    {
        // TODO: what about culture?
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        IContentType contentType = GetContentType(contentTypeAlias)
            // causes rollback
            ?? throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias));

        var content = new Content(name, parent, contentType, userId);

        return content;
    }

    /// <summary>
    ///     Creates an <see cref="IContent" /> object of a specified content type.
    /// </summary>
    /// <remarks>This method returns a new, persisted, IContent with an identity.</remarks>
    /// <param name="name">The name of the content object.</param>
    /// <param name="parentId">The identifier of the parent, or -1.</param>
    /// <param name="contentTypeAlias">The alias of the content type.</param>
    /// <param name="userId">The optional id of the user creating the content.</param>
    /// <returns>The content object.</returns>
    public IContent CreateAndSave(string name, int parentId, string contentTypeAlias, int userId = Constants.Security.SuperUserId)
    {
        // TODO: what about culture?
        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            // locking the content tree secures content types too
            scope.WriteLock(Constants.Locks.ContentTree);

            IContentType contentType = GetContentType(contentTypeAlias)
                // + locks
                ??
                // causes rollback
                throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias));

            IContent? parent = parentId > 0 ? GetById(parentId) : null; // + locks
            if (parentId > 0 && parent == null)
            {
                throw new ArgumentException("No content with that id.", nameof(parentId)); // causes rollback
            }

            Content content = parentId > 0
                ? new Content(name, parent!, contentType, userId)
                : new Content(name, parentId, contentType, userId);

            Save(content, userId);

            scope.Complete();

            return content;
        }
    }

    /// <summary>
    ///     Creates an <see cref="IContent" /> object of a specified content type, under a parent.
    /// </summary>
    /// <remarks>This method returns a new, persisted, IContent with an identity.</remarks>
    /// <param name="name">The name of the content object.</param>
    /// <param name="parent">The parent content object.</param>
    /// <param name="contentTypeAlias">The alias of the content type.</param>
    /// <param name="userId">The optional id of the user creating the content.</param>
    /// <returns>The content object.</returns>
    public IContent CreateAndSave(string name, IContent parent, string contentTypeAlias, int userId = Constants.Security.SuperUserId)
    {
        // TODO: what about culture?
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            // locking the content tree secures content types too
            scope.WriteLock(Constants.Locks.ContentTree);

            IContentType contentType = GetContentType(contentTypeAlias)
            // + locks
                ??
                // causes rollback
                throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias));

            var content = new Content(name, parent, contentType, userId);

            Save(content, userId);

            scope.Complete();
            return content;
        }
    }

    #endregion

    #region Get, Has, Is

    /// <summary>
    /// </summary>
    /// <param name="contents"></param>
    /// <param name="userId"></param>
    /// <returns></returns>
    Attempt<OperationResult?> IContentServiceBase<IContent>.Save(IEnumerable<IContent> contents, int userId) =>
        Attempt.Succeed(Save(contents, userId));

    /// <summary>
    ///     Gets a collection of <see cref="IContent" /> objects by Level
    /// </summary>
    /// <param name="level">The level to retrieve Content from</param>
    /// <returns>An Enumerable list of <see cref="IContent" /> objects</returns>
    /// <remarks>Contrary to most methods, this method filters out trashed content items.</remarks>
    public IEnumerable<IContent> GetByLevel(int level)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            IQuery<IContent>? query = Query<IContent>().Where(x => x.Level == level && x.Trashed == false);
            return _documentRepository.Get(query);
        }
    }

    /// <summary>
    ///     Gets a collection of <see cref="IContent" /> objects, which are ancestors of the current content.
    /// </summary>
    /// <param name="id">Id of the <see cref="IContent" /> to retrieve ancestors for</param>
    /// <returns>An Enumerable list of <see cref="IContent" /> objects</returns>
    public IEnumerable<IContent> GetAncestors(int id)
    {
        // intentionally not locking
        IContent? content = GetById(id);
        if (content is null)
        {
            return Enumerable.Empty<IContent>();
        }

        return GetAncestors(content);
    }

    /// <summary>
    ///     Gets a collection of <see cref="IContent" /> objects, which are ancestors of the current content.
    /// </summary>
    /// <param name="content"><see cref="IContent" /> to retrieve ancestors for</param>
    /// <returns>An Enumerable list of <see cref="IContent" /> objects</returns>
    public IEnumerable<IContent> GetAncestors(IContent content)
    {
        // null check otherwise we get exceptions
        if (content.Path.IsNullOrWhiteSpace())
        {
            return Enumerable.Empty<IContent>();
        }

        var ids = content.GetAncestorIds()?.ToArray();
        if (ids?.Any() == false)
        {
            return new List<IContent>();
        }

        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            return _documentRepository.GetMany(ids!);
        }
    }

    /// <summary>
    ///     Gets a collection of published <see cref="IContent" /> objects by Parent Id
    /// </summary>
    /// <param name="id">Id of the Parent to retrieve Children from</param>
    /// <returns>An Enumerable list of published <see cref="IContent" /> objects</returns>
    public IEnumerable<IContent> GetPublishedChildren(int id)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            IQuery<IContent>? query = Query<IContent>().Where(x => x.ParentId == id && x.Published);
            return _documentRepository.Get(query).OrderBy(x => x.SortOrder);
        }
    }

    /// <inheritdoc />
    public IEnumerable<IContent> GetPagedChildren(int id, long pageIndex, int pageSize, out long totalChildren, IQuery<IContent>? filter = null, Ordering? ordering = null)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        ordering ??= Ordering.By("sortOrder");

        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);

            IQuery<IContent>? query = Query<IContent>()?.Where(x => x.ParentId == id);
            return _documentRepository.GetPage(query, pageIndex, pageSize, out totalChildren, filter, ordering);
        }
    }

    /// <inheritdoc />
    public IEnumerable<IContent> GetPagedDescendants(int id, long pageIndex, int pageSize, out long totalChildren, IQuery<IContent>? filter = null, Ordering? ordering = null)
    {
        ordering ??= Ordering.By("Path");

        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);

            // if the id is System Root, then just get all
            if (id != Constants.System.Root)
            {
                TreeEntityPath[] contentPath =
                    _entityRepository.GetAllPaths(Constants.ObjectTypes.Document, id).ToArray();
                if (contentPath.Length == 0)
                {
                    totalChildren = 0;
                    return Enumerable.Empty<IContent>();
                }

                return GetPagedLocked(GetPagedDescendantQuery(contentPath[0].Path), pageIndex, pageSize, out totalChildren, filter, ordering);
            }

            return GetPagedLocked(null, pageIndex, pageSize, out totalChildren, filter, ordering);
        }
    }

    private IQuery<IContent>? GetPagedDescendantQuery(string contentPath)
    {
        IQuery<IContent>? query = Query<IContent>();
        if (!contentPath.IsNullOrWhiteSpace())
        {
            query?.Where(x => x.Path.SqlStartsWith($"{contentPath},", TextColumnType.NVarchar));
        }

        return query;
    }

    private IEnumerable<IContent> GetPagedLocked(IQuery<IContent>? query, long pageIndex, int pageSize, out long totalChildren, IQuery<IContent>? filter, Ordering? ordering)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (ordering == null)
        {
            throw new ArgumentNullException(nameof(ordering));
        }

        return _documentRepository.GetPage(query, pageIndex, pageSize, out totalChildren, filter, ordering);
    }

    /// <summary>
    ///     Gets the parent of the current content as an <see cref="IContent" /> item.
    /// </summary>
    /// <param name="id">Id of the <see cref="IContent" /> to retrieve the parent from</param>
    /// <returns>Parent <see cref="IContent" /> object</returns>
    public IContent? GetParent(int id)
    {
        // intentionally not locking
        IContent? content = GetById(id);
        return GetParent(content);
    }

    /// <summary>
    ///     Gets a collection of <see cref="IContent" /> objects, which reside at the first level / root
    /// </summary>
    /// <returns>An Enumerable list of <see cref="IContent" /> objects</returns>
    public IEnumerable<IContent> GetRootContent()
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            IQuery<IContent> query = Query<IContent>().Where(x => x.ParentId == Constants.System.Root);
            return _documentRepository.Get(query);
        }
    }

    /// <summary>
    ///     Gets all published content items
    /// </summary>
    /// <returns></returns>
    internal IEnumerable<IContent> GetAllPublished()
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            return _documentRepository.Get(QueryNotTrashed);
        }
    }

    /// <summary>
    ///     Gets a collection of an <see cref="IContent" /> objects, which resides in the Recycle Bin
    /// </summary>
    /// <returns>An Enumerable list of <see cref="IContent" /> objects</returns>
    public IEnumerable<IContent> GetPagedContentInRecycleBin(long pageIndex, int pageSize, out long totalRecords, IQuery<IContent>? filter = null, Ordering? ordering = null)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            ordering ??= Ordering.By("Path");

            scope.ReadLock(Constants.Locks.ContentTree);
            IQuery<IContent>? query = Query<IContent>()?
                .Where(x => x.Path.StartsWith(Constants.System.RecycleBinContentPathPrefix));
            return _documentRepository.GetPage(query, pageIndex, pageSize, out totalRecords, filter, ordering);
        }
    }

    /// <summary>
    ///     Checks if the passed in <see cref="IContent" /> can be published based on the ancestors publish state.
    /// </summary>
    /// <param name="content"><see cref="IContent" /> to check if ancestors are published</param>
    /// <returns>True if the Content can be published, otherwise False</returns>
    public bool IsPathPublishable(IContent content)
    {
        // fast
        if (content.ParentId == Constants.System.Root)
        {
            return true; // root content is always publishable
        }

        if (content.Trashed)
        {
            return false; // trashed content is never publishable
        }

        // not trashed and has a parent: publishable if the parent is path-published
        IContent? parent = GetById(content.ParentId);
        return parent == null || IsPathPublished(parent);
    }

    #endregion

    #region Save, Publish, Unpublish

    /// <summary>
    ///     Publishes/unpublishes any pending publishing changes made to the document.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This MUST NOT be called from within this service, this used to be a public API and must only be used outside of
    ///         this service.
    ///         Internally in this service, calls must be made to CommitDocumentChangesInternal
    ///     </para>
    ///     <para>This is the underlying logic for both publishing and unpublishing any document</para>
    ///     <para>
    ///         Pending publishing/unpublishing changes on a document are made with calls to
    ///         <see cref="ContentRepositoryExtensions.PublishCulture" /> and
    ///         <see cref="ContentRepositoryExtensions.UnpublishCulture" />.
    ///     </para>
    ///     <para>
    ///         When publishing or unpublishing a single culture, or all cultures, use <see cref="SaveAndPublish" />
    ///         and <see cref="Unpublish" />. But if the flexibility to both publish and unpublish in a single operation is
    ///         required
    ///         then this method needs to be used in combination with <see cref="ContentRepositoryExtensions.PublishCulture" />
    ///         and <see cref="ContentRepositoryExtensions.UnpublishCulture" />
    ///         on the content itself - this prepares the content, but does not commit anything - and then, invoke
    ///         <see cref="CommitDocumentChanges" /> to actually commit the changes to the database.
    ///     </para>
    ///     <para>The document is *always* saved, even when publishing fails.</para>
    /// </remarks>
    internal PublishResult CommitDocumentChanges(IContent content, int userId = Constants.Security.SuperUserId)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            EventMessages evtMsgs = EventMessagesFactory.Get();

            scope.WriteLock(Constants.Locks.ContentTree);

            var savingNotification = new ContentSavingNotification(content, evtMsgs);
            if (scope.Notifications.PublishCancelable(savingNotification))
            {
                return new PublishResult(PublishResultType.FailedPublishCancelledByEvent, evtMsgs, content);
            }

            var allLangs = _languageRepository.GetMany().ToList();

            PublishResult result = CommitDocumentChangesInternal(scope, content, evtMsgs, allLangs, savingNotification.State, userId);
            scope.Complete();
            return result;
        }
    }

    // utility 'PublishCultures' func used by SaveAndPublishBranch
    private bool PublishBranch_PublishCultures(IContent content, HashSet<string> culturesToPublish, IReadOnlyCollection<ILanguage> allLangs)
    {
        // variant content type - publish specified cultures
        // invariant content type - publish only the invariant culture

        var publishTime = DateTime.Now;
        if (content.ContentType.VariesByCulture())
        {
            return culturesToPublish.All(culture =>
            {
                CultureImpact? impact = _cultureImpactFactory.Create(culture, IsDefaultCulture(allLangs, culture), content);
                return content.PublishCulture(impact, publishTime, _propertyEditorCollection) &&
                       _propertyValidationService.Value.IsPropertyDataValid(content, out _, impact);
            });
        }

        return content.PublishCulture(_cultureImpactFactory.ImpactInvariant(), publishTime, _propertyEditorCollection)
               && _propertyValidationService.Value.IsPropertyDataValid(content, out _, _cultureImpactFactory.ImpactInvariant());
    }

    // utility 'ShouldPublish' func used by PublishBranch
    private static HashSet<string>? PublishBranch_ShouldPublish(ref HashSet<string>? cultures, string c, bool published, bool edited, bool isRoot, PublishBranchFilter publishBranchFilter)
    {
        // if published, republish
        if (published)
        {
            cultures ??= new HashSet<string>(); // empty means 'already published'

            if (edited || publishBranchFilter.HasFlag(PublishBranchFilter.ForceRepublish))
            {
                cultures.Add(c); // <culture> means 'republish this culture'
            }

            return cultures;
        }

        // if not published, publish if force/root else do nothing
        if (!publishBranchFilter.HasFlag(PublishBranchFilter.IncludeUnpublished) && !isRoot)
        {
            return cultures; // null means 'nothing to do'
        }

        cultures ??= new HashSet<string>();

        cultures.Add(c); // <culture> means 'publish this culture'
        return cultures;
    }

    /// <inheritdoc />
    [Obsolete("This method is not longer used as the 'force' parameter has been split into publishing unpublished and force re-published. Please use the overload containing parameters for those options instead. Scheduled for removal in V17")]
    public IEnumerable<PublishResult> PublishBranch(IContent content, bool force, string[] cultures, int userId = Constants.Security.SuperUserId)
        => PublishBranch(content, force ? PublishBranchFilter.IncludeUnpublished : PublishBranchFilter.Default, cultures, userId);

    /// <inheritdoc />
    public IEnumerable<PublishResult> PublishBranch(IContent content, PublishBranchFilter publishBranchFilter, string[] cultures, int userId = Constants.Security.SuperUserId)
    {
        // note: EditedValue and PublishedValue are objects here, so it is important to .Equals()
        // and not to == them, else we would be comparing references, and that is a bad thing

        cultures = EnsureCultures(content, cultures);

        string? defaultCulture;
        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            defaultCulture = _languageRepository.GetDefaultIsoCode();
            scope.Complete();
        }

        // determines cultures to be published
        // can be: null (content is not impacted), an empty set (content is impacted but already published), or cultures
        HashSet<string>? ShouldPublish(IContent c)
        {
            var isRoot = c.Id == content.Id;
            HashSet<string>? culturesToPublish = null;

            // invariant content type
            if (!c.ContentType.VariesByCulture())
            {
                return PublishBranch_ShouldPublish(ref culturesToPublish, "*", c.Published, c.Edited, isRoot, publishBranchFilter);
            }

            // variant content type, specific cultures
            if (c.Published)
            {
                // then some (and maybe all) cultures will be 'already published' (unless forcing),
                // others will have to 'republish this culture'
                foreach (var culture in cultures)
                {
                    // We could be publishing a parent invariant page, with descendents that are variant.
                    // So convert the invariant request to a request for the default culture.
                    var specificCulture = culture == "*" ? defaultCulture : culture;

                    PublishBranch_ShouldPublish(ref culturesToPublish, specificCulture, c.IsCulturePublished(specificCulture), c.IsCultureEdited(specificCulture), isRoot, publishBranchFilter);
                }

                return culturesToPublish;
            }

            // if not published, publish if forcing unpublished/root else do nothing
            return publishBranchFilter.HasFlag(PublishBranchFilter.IncludeUnpublished) || isRoot
                ? new HashSet<string>(cultures) // means 'publish specified cultures'
                : null; // null means 'nothing to do'
        }

        return PublishBranch(content, ShouldPublish, PublishBranch_PublishCultures, userId);
    }

    private static string[] EnsureCultures(IContent content, string[] cultures)
    {
        // Ensure consistent indication of "all cultures" for variant content.
        if (content.ContentType.VariesByCulture() is false && ProvidedCulturesIndicatePublishAll(cultures))
        {
            cultures = ["*"];
        }

        return cultures.Select(x => x.EnsureCultureCode()!).ToArray();
    }

    private static bool ProvidedCulturesIndicatePublishAll(string[] cultures) => cultures.Length == 0 || (cultures.Length == 1 && cultures[0] == "invariant");

    internal IEnumerable<PublishResult> PublishBranch(
        IContent document,
        Func<IContent, HashSet<string>?> shouldPublish,
        Func<IContent, HashSet<string>, IReadOnlyCollection<ILanguage>, bool> publishCultures,
        int userId = Constants.Security.SuperUserId)
    {
        if (shouldPublish == null)
        {
            throw new ArgumentNullException(nameof(shouldPublish));
        }

        if (publishCultures == null)
        {
            throw new ArgumentNullException(nameof(publishCultures));
        }

        EventMessages eventMessages = EventMessagesFactory.Get();
        var results = new List<PublishResult>();
        var publishedDocuments = new List<IContent>();

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            var allLangs = _languageRepository.GetMany().ToList();

            if (!document.HasIdentity)
            {
                throw new InvalidOperationException("Cannot not branch-publish a new document.");
            }

            PublishedState publishedState = document.PublishedState;
            if (publishedState == PublishedState.Publishing)
            {
                throw new InvalidOperationException("Cannot mix PublishCulture and SaveAndPublishBranch.");
            }

            // deal with the branch root - if it fails, abort
            HashSet<string>? culturesToPublish = shouldPublish(document);
            PublishResult? result = PublishBranchItem(scope, document, culturesToPublish, publishCultures, true, publishedDocuments, eventMessages, userId, allLangs, out IDictionary<string, object?>? notificationState);
            if (result != null)
            {
                results.Add(result);
                if (!result.Success)
                {
                    return results;
                }
            }

            HashSet<string> culturesPublished = culturesToPublish ?? [];

            // deal with descendants
            // if one fails, abort its branch
            var exclude = new HashSet<int>();

            int count;
            var page = 0;
            const int pageSize = 100;
            do
            {
                count = 0;

                // important to order by Path ASC so make it explicit in case defaults change
                // ReSharper disable once RedundantArgumentDefaultValue
                foreach (IContent d in GetPagedDescendants(document.Id, page, pageSize, out _, ordering: Ordering.By("Path", Direction.Ascending)))
                {
                    count++;

                    // if parent is excluded, exclude child too
                    if (exclude.Contains(d.ParentId))
                    {
                        exclude.Add(d.Id);
                        continue;
                    }

                    // no need to check path here, parent has to be published here
                    culturesToPublish = shouldPublish(d);
                    result = PublishBranchItem(scope, d, culturesToPublish, publishCultures, false, publishedDocuments, eventMessages, userId, allLangs, out _);
                    if (result != null)
                    {
                        results.Add(result);
                        if (result.Success)
                        {
                            culturesPublished.UnionWith(culturesToPublish ?? []);
                            continue;
                        }
                    }

                    // if we could not publish the document, cut its branch
                    exclude.Add(d.Id);
                }

                page++;
            }
            while (count > 0);

            Audit(AuditType.Publish, userId, document.Id, "Branch published");

            // trigger events for the entire branch
            // (SaveAndPublishBranchOne does *not* do it)
            var variesByCulture = document.ContentType.VariesByCulture();
            scope.Notifications.Publish(
                new ContentTreeChangeNotification(
                    document,
                    TreeChangeTypes.RefreshBranch,
                    variesByCulture ? culturesPublished.IsCollectionEmpty() ? null : culturesPublished : ["*"],
                    null,
                    eventMessages));
            scope.Notifications.Publish(new ContentPublishedNotification(publishedDocuments, eventMessages).WithState(notificationState));

            scope.Complete();
        }

        return results;
    }

    // shouldPublish: a function determining whether the document has changes that need to be published
    //  note - 'force' is handled by 'editing'
    // publishValues: a function publishing values (using the appropriate PublishCulture calls)
    private PublishResult? PublishBranchItem(
        ICoreScope scope,
        IContent document,
        HashSet<string>? culturesToPublish,
        Func<IContent, HashSet<string>, IReadOnlyCollection<ILanguage>,
            bool> publishCultures,
        bool isRoot,
        ICollection<IContent> publishedDocuments,
        EventMessages evtMsgs,
        int userId,
        IReadOnlyCollection<ILanguage> allLangs,
        out IDictionary<string, object?>? initialNotificationState)
    {
        initialNotificationState = new Dictionary<string, object?>();

        // we need to guard against unsaved changes before proceeding; the document will be saved, but we're not firing any saved notifications
        if (HasUnsavedChanges(document))
        {
            return new PublishResult(PublishResultType.FailedPublishUnsavedChanges, evtMsgs, document);
        }

        // null = do not include
        if (culturesToPublish == null)
        {
            return null;
        }

        // empty = already published
        if (culturesToPublish.Count == 0)
        {
            return new PublishResult(PublishResultType.SuccessPublishAlready, evtMsgs, document);
        }

        var savingNotification = new ContentSavingNotification(document, evtMsgs);
        if (scope.Notifications.PublishCancelable(savingNotification))
        {
            return new PublishResult(PublishResultType.FailedPublishCancelledByEvent, evtMsgs, document);
        }

        // publish & check if values are valid
        if (!publishCultures(document, culturesToPublish, allLangs))
        {
            // TODO: Based on this callback behavior there is no way to know which properties may have been invalid if this failed, see other results of FailedPublishContentInvalid
            return new PublishResult(PublishResultType.FailedPublishContentInvalid, evtMsgs, document);
        }

        PublishResult result = CommitDocumentChangesInternal(scope, document, evtMsgs, allLangs, savingNotification.State, userId, true, isRoot);
        if (result.Success)
        {
            publishedDocuments.Add(document);
        }

        return result;
    }

    #endregion

    #region Move, RecycleBin

    /// <inheritdoc />
    public OperationResult MoveToRecycleBin(IContent content, int userId = Constants.Security.SuperUserId)
    {
        EventMessages eventMessages = EventMessagesFactory.Get();
        var moves = new List<(IContent, string)>();

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            var originalPath = content.Path;
            var moveEventInfo =
                new MoveToRecycleBinEventInfo<IContent>(content, originalPath);

            var movingToRecycleBinNotification =
                new ContentMovingToRecycleBinNotification(moveEventInfo, eventMessages);
            if (scope.Notifications.PublishCancelable(movingToRecycleBinNotification))
            {
                scope.Complete();
                return OperationResult.Cancel(eventMessages); // causes rollback
            }

            // if it's published we may want to force-unpublish it - that would be backward-compatible... but...
            // making a radical decision here: trashing is equivalent to moving under an unpublished node so
            // it's NOT unpublishing, only the content is now masked - allowing us to restore it if wanted
            // if (content.HasPublishedVersion)
            // { }
            PerformMoveLocked(content, Constants.System.RecycleBinContent, null, userId, moves, true);
            scope.Notifications.Publish(
                new ContentTreeChangeNotification(content, TreeChangeTypes.RefreshBranch, eventMessages));

            MoveToRecycleBinEventInfo<IContent>[] moveInfo = moves
                .Select(x => new MoveToRecycleBinEventInfo<IContent>(x.Item1, x.Item2))
                .ToArray();

            scope.Notifications.Publish(
                new ContentMovedToRecycleBinNotification(moveInfo, eventMessages).WithStateFrom(
                    movingToRecycleBinNotification));
            Audit(AuditType.Move, userId, content.Id, "Moved to recycle bin");

            scope.Complete();
        }

        return OperationResult.Succeed(eventMessages);
    }

    /// <summary>
    ///     Moves an <see cref="IContent" /> object to a new location by changing its parent id.
    /// </summary>
    /// <remarks>
    ///     If the <see cref="IContent" /> object is already published it will be
    ///     published after being moved to its new location. Otherwise it'll just
    ///     be saved with a new parent id.
    /// </remarks>
    /// <param name="content">The <see cref="IContent" /> to move</param>
    /// <param name="parentId">Id of the Content's new Parent</param>
    /// <param name="userId">Optional Id of the User moving the Content</param>
    public OperationResult Move(IContent content, int parentId, int userId = Constants.Security.SuperUserId)
    {
        EventMessages eventMessages = EventMessagesFactory.Get();

        if (content.ParentId == parentId)
        {
            return OperationResult.Succeed(eventMessages);
        }

        // if moving to the recycle bin then use the proper method
        if (parentId == Constants.System.RecycleBinContent)
        {
            return MoveToRecycleBin(content, userId);
        }

        var moves = new List<(IContent, string)>();

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            IContent? parent = parentId == Constants.System.Root ? null : GetById(parentId);
            if (parentId != Constants.System.Root && (parent == null || parent.Trashed))
            {
                throw new InvalidOperationException("Parent does not exist or is trashed."); // causes rollback
            }

            TryGetParentKey(parentId, out Guid? parentKey);
            var moveEventInfo = new MoveEventInfo<IContent>(content, content.Path, parentId, parentKey);

            var movingNotification = new ContentMovingNotification(moveEventInfo, eventMessages);
            if (scope.Notifications.PublishCancelable(movingNotification))
            {
                scope.Complete();
                return OperationResult.Cancel(eventMessages); // causes rollback
            }

            // if content was trashed, and since we're not moving to the recycle bin,
            // indicate that the trashed status should be changed to false, else just
            // leave it unchanged
            var trashed = content.Trashed ? false : (bool?)null;

            // if the content was trashed under another content, and so has a published version,
            // it cannot move back as published but has to be unpublished first - that's for the
            // root content, everything underneath will retain its published status
            if (content.Trashed && content.Published)
            {
                // however, it had been masked when being trashed, so there's no need for
                // any special event here - just change its state
                content.PublishedState = PublishedState.Unpublishing;
            }

            PerformMoveLocked(content, parentId, parent, userId, moves, trashed);

            scope.Notifications.Publish(
                new ContentTreeChangeNotification(content, TreeChangeTypes.RefreshBranch, eventMessages));

            // changes
            MoveEventInfo<IContent>[] moveInfo = moves
                .Select(x =>
                {
                    TryGetParentKey(x.Item1.ParentId, out Guid? itemParentKey);
                    return new MoveEventInfo<IContent>(x.Item1, x.Item2, x.Item1.ParentId, itemParentKey);
                })
                .ToArray();

            scope.Notifications.Publish(
                new ContentMovedNotification(moveInfo, eventMessages).WithStateFrom(movingNotification));

            Audit(AuditType.Move, userId, content.Id);

            scope.Complete();
            return OperationResult.Succeed(eventMessages);
        }
    }

    // MUST be called from within WriteLock
    // trash indicates whether we are trashing, un-trashing, or not changing anything
    private void PerformMoveLocked(IContent content, int parentId, IContent? parent, int userId, ICollection<(IContent, string)> moves, bool? trash)
    {
        content.WriterId = userId;
        content.ParentId = parentId;

        // get the level delta (old pos to new pos)
        // note that recycle bin (id:-20) level is 0!
        var levelDelta = 1 - content.Level + (parent?.Level ?? 0);

        var paths = new Dictionary<int, string>();

        moves.Add((content, content.Path)); // capture original path

        // need to store the original path to lookup descendants based on it below
        var originalPath = content.Path;

        // these will be updated by the repo because we changed parentId
        // content.Path = (parent == null ? "-1" : parent.Path) + "," + content.Id;
        // content.SortOrder = ((ContentRepository) repository).NextChildSortOrder(parentId);
        // content.Level += levelDelta;
        PerformMoveContentLocked(content, userId, trash);

        // if uow is not immediate, content.Path will be updated only when the UOW commits,
        // and because we want it now, we have to calculate it by ourselves
        // paths[content.Id] = content.Path;
        paths[content.Id] =
            (parent == null
                ? parentId == Constants.System.RecycleBinContent ? "-1,-20" : Constants.System.RootString
                : parent.Path) + "," + content.Id;

        const int pageSize = 500;
        IQuery<IContent>? query = GetPagedDescendantQuery(originalPath);
        long total;
        do
        {
            // We always page a page 0 because for each page, we are moving the result so the resulting total will be reduced
            IEnumerable<IContent> descendants =
                GetPagedLocked(query, 0, pageSize, out total, null, Ordering.By("Path"));

            foreach (IContent descendant in descendants)
            {
                moves.Add((descendant, descendant.Path)); // capture original path

                // update path and level since we do not update parentId
                descendant.Path = paths[descendant.Id] = paths[descendant.ParentId] + "," + descendant.Id;
                descendant.Level += levelDelta;
                PerformMoveContentLocked(descendant, userId, trash);
            }
        }
        while (total > pageSize);
    }

    private void PerformMoveContentLocked(IContent content, int userId, bool? trash)
    {
        if (trash.HasValue)
        {
            ((ContentBase)content).Trashed = trash.Value;
        }

        content.WriterId = userId;
        _documentRepository.Save(content);
    }

    public async Task<OperationResult> EmptyRecycleBinAsync(Guid userId)
        => EmptyRecycleBin(await _userIdKeyResolver.GetAsync(userId));

    /// <summary>
    ///     Empties the Recycle Bin by deleting all <see cref="IContent" /> that resides in the bin
    /// </summary>
    public OperationResult EmptyRecycleBin(int userId = Constants.Security.SuperUserId)
    {
        var deleted = new List<IContent>();
        EventMessages eventMessages = EventMessagesFactory.Get();

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            // emptying the recycle bin means deleting whatever is in there - do it properly!
            IQuery<IContent>? query = Query<IContent>().Where(x => x.ParentId == Constants.System.RecycleBinContent);
            IContent[] contents = _documentRepository.Get(query).ToArray();

            var emptyingRecycleBinNotification = new ContentEmptyingRecycleBinNotification(contents, eventMessages);
            var deletingContentNotification = new ContentDeletingNotification(contents, eventMessages);
            if (scope.Notifications.PublishCancelable(emptyingRecycleBinNotification) || scope.Notifications.PublishCancelable(deletingContentNotification))
            {
                scope.Complete();
                return OperationResult.Cancel(eventMessages);
            }

            if (contents is not null)
            {
                foreach (IContent content in contents)
                {
                    if (_contentSettings.DisableDeleteWhenReferenced && _relationService.IsRelated(content.Id, RelationDirectionFilter.Child))
                    {
                        continue;
                    }

                    DeleteLocked(scope, content, eventMessages);
                    deleted.Add(content);
                }
            }

            scope.Notifications.Publish(
                new ContentEmptiedRecycleBinNotification(deleted, eventMessages).WithStateFrom(
                    emptyingRecycleBinNotification));
            scope.Notifications.Publish(
                new ContentTreeChangeNotification(deleted, TreeChangeTypes.Remove, eventMessages));
            Audit(AuditType.Delete, userId, Constants.System.RecycleBinContent, "Recycle bin emptied");

            scope.Complete();
        }

        return OperationResult.Succeed(eventMessages);
    }

    public bool RecycleBinSmells()
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            return _documentRepository.RecycleBinSmells();
        }
    }

    #endregion

    #region Others

    /// <summary>
    ///     Copies an <see cref="IContent" /> object by creating a new Content object of the same type and copies all data from
    ///     the current
    ///     to the new copy which is returned. Recursively copies all children.
    /// </summary>
    /// <param name="content">The <see cref="IContent" /> to copy</param>
    /// <param name="parentId">Id of the Content's new Parent</param>
    /// <param name="relateToOriginal">Boolean indicating whether the copy should be related to the original</param>
    /// <param name="userId">Optional Id of the User copying the Content</param>
    /// <returns>The newly created <see cref="IContent" /> object</returns>
    public IContent? Copy(IContent content, int parentId, bool relateToOriginal, int userId = Constants.Security.SuperUserId) => Copy(content, parentId, relateToOriginal, true, userId);

    /// <summary>
    ///     Copies an <see cref="IContent" /> object by creating a new Content object of the same type and copies all data from
    ///     the current
    ///     to the new copy which is returned.
    /// </summary>
    /// <param name="content">The <see cref="IContent" /> to copy</param>
    /// <param name="parentId">Id of the Content's new Parent</param>
    /// <param name="relateToOriginal">Boolean indicating whether the copy should be related to the original</param>
    /// <param name="recursive">A value indicating whether to recursively copy children.</param>
    /// <param name="userId">Optional Id of the User copying the Content</param>
    /// <returns>The newly created <see cref="IContent" /> object</returns>
    public IContent? Copy(IContent content, int parentId, bool relateToOriginal, bool recursive, int userId = Constants.Security.SuperUserId)
    {
        EventMessages eventMessages = EventMessagesFactory.Get();

        // keep track of updates (copied item key and parent key) for the in-memory navigation structure
        var navigationUpdates = new List<Tuple<Guid, Guid?>>();

        IContent copy = content.DeepCloneWithResetIdentities();
        copy.ParentId = parentId;

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            TryGetParentKey(parentId, out Guid? parentKey);
            if (scope.Notifications.PublishCancelable(new ContentCopyingNotification(content, copy, parentId, parentKey, eventMessages)))
            {
                scope.Complete();
                return null;
            }

            // note - relateToOriginal is not managed here,
            // it's just part of the Copied event args so the RelateOnCopyHandler knows what to do
            // meaning that the event has to trigger for every copied content including descendants
            var copies = new List<Tuple<IContent, IContent>>();

            scope.WriteLock(Constants.Locks.ContentTree);

            // a copy is not published (but not really unpublishing either)
            // update the create author and last edit author
            if (copy.Published)
            {
                copy.Published = false;
            }

            copy.CreatorId = userId;
            copy.WriterId = userId;

            // get the current permissions, if there are any explicit ones they need to be copied
            EntityPermissionCollection currentPermissions = GetPermissions(content);
            currentPermissions.RemoveWhere(p => p.IsDefaultPermissions);

            // save and flush because we need the ID for the recursive Copying events
            _documentRepository.Save(copy);

            // store navigation update information for copied item
            navigationUpdates.Add(Tuple.Create(copy.Key, GetParent(copy)?.Key));

            // add permissions
            if (currentPermissions.Count > 0)
            {
                var permissionSet = new ContentPermissionSet(copy, currentPermissions);
                _documentRepository.AddOrUpdatePermissions(permissionSet);
            }

            // keep track of copies
            copies.Add(Tuple.Create(content, copy));
            var idmap = new Dictionary<int, int> { [content.Id] = copy.Id };

            // process descendants
            if (recursive)
            {
                const int pageSize = 500;
                var page = 0;
                var total = long.MaxValue;
                while (page * pageSize < total)
                {
                    IEnumerable<IContent> descendants =
                        GetPagedDescendants(content.Id, page++, pageSize, out total);
                    foreach (IContent descendant in descendants)
                    {
                        // when copying a branch into itself, the copy of a root would be seen as a descendant
                        // and would be copied again => filter it out.
                        if (descendant.Id == copy.Id)
                        {
                            continue;
                        }

                        // if parent has not been copied, skip, else gets its copy id
                        if (idmap.TryGetValue(descendant.ParentId, out parentId) == false)
                        {
                            continue;
                        }

                        IContent descendantCopy = descendant.DeepCloneWithResetIdentities();
                        descendantCopy.ParentId = parentId;

                        if (scope.Notifications.PublishCancelable(new ContentCopyingNotification(descendant, descendantCopy, parentId, parentKey, eventMessages)))
                        {
                            continue;
                        }

                        // a copy is not published (but not really unpublishing either)
                        // update the create author and last edit author
                        if (descendantCopy.Published)
                        {
                            descendantCopy.Published = false;
                        }

                        descendantCopy.CreatorId = userId;
                        descendantCopy.WriterId = userId;

                        // since the repository relies on the dirty state to figure out whether it needs to update the sort order, we mark it dirty here
                        descendantCopy.SortOrder = descendantCopy.SortOrder;

                        // save and flush (see above)
                        _documentRepository.Save(descendantCopy);

                        // store navigation update information for descendants
                        navigationUpdates.Add(Tuple.Create(descendantCopy.Key, GetParent(descendantCopy)?.Key));

                        copies.Add(Tuple.Create(descendant, descendantCopy));
                        idmap[descendant.Id] = descendantCopy.Id;
                    }
                }
            }

            // not handling tags here, because
            // - tags should be handled by the content repository
            // - a copy is unpublished and therefore has no impact on tags in DB
            scope.Notifications.Publish(
                new ContentTreeChangeNotification(copy, TreeChangeTypes.RefreshBranch, eventMessages));
            foreach (Tuple<IContent, IContent> x in CollectionsMarshal.AsSpan(copies))
            {
                scope.Notifications.Publish(new ContentCopiedNotification(x.Item1, x.Item2, parentId, parentKey, relateToOriginal, eventMessages));
            }

            Audit(AuditType.Copy, userId, content.Id);

            scope.Complete();
        }

        return copy;
    }

    private bool TryGetParentKey(int parentId, [NotNullWhen(true)] out Guid? parentKey)
    {
        Attempt<Guid> parentKeyAttempt = _idKeyMap.GetKeyForId(parentId, UmbracoObjectTypes.Document);
        parentKey = parentKeyAttempt.Success ? parentKeyAttempt.Result : null;
        return parentKeyAttempt.Success;
    }

    /// <summary>
    ///     Sends an <see cref="IContent" /> to Publication, which executes handlers and events for the 'Send to Publication'
    ///     action.
    /// </summary>
    /// <param name="content">The <see cref="IContent" /> to send to publication</param>
    /// <param name="userId">Optional Id of the User issuing the send to publication</param>
    /// <returns>True if sending publication was successful otherwise false</returns>
    public bool SendToPublication(IContent? content, int userId = Constants.Security.SuperUserId)
    {
        if (content is null)
        {
            return false;
        }

        EventMessages evtMsgs = EventMessagesFactory.Get();

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            var sendingToPublishNotification = new ContentSendingToPublishNotification(content, evtMsgs);
            if (scope.Notifications.PublishCancelable(sendingToPublishNotification))
            {
                scope.Complete();
                return false;
            }

            // track the cultures changing for auditing
            var culturesChanging = content.ContentType.VariesByCulture()
                ? string.Join(",", content.CultureInfos!.Values.Where(x => x.IsDirty()).Select(x => x.Culture))
                : null;

            // TODO: Currently there's no way to change track which variant properties have changed, we only have change
            // tracking enabled on all values on the Property which doesn't allow us to know which variants have changed.
            // in this particular case, determining which cultures have changed works with the above with names since it will
            // have always changed if it's been saved in the back office but that's not really fail safe.

            // Save before raising event
            OperationResult saveResult = Save(content, userId);

            // always complete (but maybe return a failed status)
            scope.Complete();

            if (!saveResult.Success)
            {
                return saveResult.Success;
            }

            scope.Notifications.Publish(
                new ContentSentToPublishNotification(content, evtMsgs).WithStateFrom(sendingToPublishNotification));

            if (culturesChanging != null)
            {
                Audit(AuditType.SendToPublishVariant, userId, content.Id, $"Send To Publish for cultures: {culturesChanging}", culturesChanging);
            }
            else
            {
                Audit(AuditType.SendToPublish, userId, content.Id);
            }

            return saveResult.Success;
        }
    }

    /// <summary>
    ///     Sorts a collection of <see cref="IContent" /> objects by updating the SortOrder according
    ///     to the ordering of items in the passed in <paramref name="items" />.
    /// </summary>
    /// <remarks>
    ///     Using this method will ensure that the Published-state is maintained upon sorting
    ///     so the cache is updated accordingly - as needed.
    /// </remarks>
    /// <param name="items"></param>
    /// <param name="userId"></param>
    /// <returns>Result indicating what action was taken when handling the command.</returns>
    public OperationResult Sort(IEnumerable<IContent> items, int userId = Constants.Security.SuperUserId)
    {
        EventMessages evtMsgs = EventMessagesFactory.Get();

        IContent[] itemsA = items.ToArray();
        if (itemsA.Length == 0)
        {
            return new OperationResult(OperationResultType.NoOperation, evtMsgs);
        }

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            OperationResult ret = Sort(scope, itemsA, userId, evtMsgs);
            scope.Complete();
            return ret;
        }
    }

    /// <summary>
    ///     Sorts a collection of <see cref="IContent" /> objects by updating the SortOrder according
    ///     to the ordering of items identified by the <paramref name="ids" />.
    /// </summary>
    /// <remarks>
    ///     Using this method will ensure that the Published-state is maintained upon sorting
    ///     so the cache is updated accordingly - as needed.
    /// </remarks>
    /// <param name="ids"></param>
    /// <param name="userId"></param>
    /// <returns>Result indicating what action was taken when handling the command.</returns>
    public OperationResult Sort(IEnumerable<int>? ids, int userId = Constants.Security.SuperUserId)
    {
        EventMessages evtMsgs = EventMessagesFactory.Get();

        var idsA = ids?.ToArray();
        if (idsA is null || idsA.Length == 0)
        {
            return new OperationResult(OperationResultType.NoOperation, evtMsgs);
        }

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);
            IContent[] itemsA = GetByIds(idsA).ToArray();

            OperationResult ret = Sort(scope, itemsA, userId, evtMsgs);
            scope.Complete();
            return ret;
        }
    }

    private OperationResult Sort(ICoreScope scope, IContent[] itemsA, int userId, EventMessages eventMessages)
    {
        var sortingNotification = new ContentSortingNotification(itemsA, eventMessages);
        var savingNotification = new ContentSavingNotification(itemsA, eventMessages);

        // raise cancelable sorting event
        if (scope.Notifications.PublishCancelable(sortingNotification))
        {
            return OperationResult.Cancel(eventMessages);
        }

        // raise cancelable saving event
        if (scope.Notifications.PublishCancelable(savingNotification))
        {
            return OperationResult.Cancel(eventMessages);
        }

        var published = new List<IContent>();
        var saved = new List<IContent>();
        var sortOrder = 0;

        foreach (IContent content in itemsA)
        {
            // if the current sort order equals that of the content we don't
            // need to update it, so just increment the sort order and continue.
            if (content.SortOrder == sortOrder)
            {
                sortOrder++;
                continue;
            }

            // else update
            content.SortOrder = sortOrder++;
            content.WriterId = userId;

            // if it's published, register it, no point running StrategyPublish
            // since we're not really publishing it and it cannot be cancelled etc
            if (content.Published)
            {
                published.Add(content);
            }

            // save
            saved.Add(content);
            _documentRepository.Save(content);
            Audit(AuditType.Sort, userId, content.Id, "Sorting content performed by user");
        }

        // first saved, then sorted
        scope.Notifications.Publish(
            new ContentSavedNotification(itemsA, eventMessages).WithStateFrom(savingNotification));
        scope.Notifications.Publish(
            new ContentSortedNotification(itemsA, eventMessages).WithStateFrom(sortingNotification));

        scope.Notifications.Publish(
            new ContentTreeChangeNotification(saved, TreeChangeTypes.RefreshNode, eventMessages));

        if (published.Any())
        {
            scope.Notifications.Publish(new ContentPublishedNotification(published, eventMessages));
        }

        return OperationResult.Succeed(eventMessages);
    }

    public ContentDataIntegrityReport CheckDataIntegrity(ContentDataIntegrityReportOptions options)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            ContentDataIntegrityReport report = _documentRepository.CheckDataIntegrity(options);

            if (report.FixedIssues.Count > 0)
            {
                // The event args needs a content item so we'll make a fake one with enough properties to not cause a null ref
                var root = new Content("root", -1, new ContentType(_shortStringHelper, -1)) { Id = -1, Key = Guid.Empty };
                scope.Notifications.Publish(new ContentTreeChangeNotification(root, TreeChangeTypes.RefreshAll, EventMessagesFactory.Get()));
            }

            scope.Complete();

            return report;
        }
    }

    #endregion

    #region Internal Methods

    /// <summary>
    ///     Gets a collection of <see cref="IContent" /> descendants by the first Parent.
    /// </summary>
    /// <param name="content"><see cref="IContent" /> item to retrieve Descendants from</param>
    /// <returns>An Enumerable list of <see cref="IContent" /> objects</returns>
    internal IEnumerable<IContent> GetPublishedDescendants(IContent content)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            return GetPublishedDescendantsLocked(content).ToArray(); // ToArray important in uow!
        }
    }

    #endregion

    #region Private Methods

    // TODO ELEMENTS: not used? clean up!
    private bool IsMandatoryCulture(IReadOnlyCollection<ILanguage> langs, string culture) =>
        langs.Any(x => x.IsMandatory && x.IsoCode.InvariantEquals(culture));

    #endregion

    #region Content Types

    /// <summary>
    ///     Deletes all content of specified type. All children of deleted content is moved to Recycle Bin.
    /// </summary>
    /// <remarks>
    ///     <para>This needs extra care and attention as its potentially a dangerous and extensive operation.</para>
    ///     <para>
    ///         Deletes content items of the specified type, and only that type. Does *not* handle content types
    ///         inheritance and compositions, which need to be managed outside of this method.
    ///     </para>
    /// </remarks>
    /// <param name="contentTypeIds">Id of the <see cref="IContentType" /></param>
    /// <param name="userId">Optional Id of the user issuing the delete operation</param>
    public void DeleteOfTypes(IEnumerable<int> contentTypeIds, int userId = Constants.Security.SuperUserId)
    {
        // TODO: This currently this is called from the ContentTypeService but that needs to change,
        // if we are deleting a content type, we should just delete the data and do this operation slightly differently.
        // This method will recursively go lookup every content item, check if any of it's descendants are
        // of a different type, move them to the recycle bin, then permanently delete the content items.
        // The main problem with this is that for every content item being deleted, events are raised...
        // which we need for many things like keeping caches in sync, but we can surely do this MUCH better.
        var changes = new List<TreeChange<IContent>>();
        var moves = new List<(IContent, string)>();
        var contentTypeIdsA = contentTypeIds.ToArray();
        EventMessages eventMessages = EventMessagesFactory.Get();

        // using an immediate uow here because we keep making changes with
        // PerformMoveLocked and DeleteLocked that must be applied immediately,
        // no point queuing operations
        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            IQuery<IContent> query = Query<IContent>().WhereIn(x => x.ContentTypeId, contentTypeIdsA);
            IContent[] contents = _documentRepository.Get(query).ToArray();

            if (contents is null)
            {
                return;
            }

            if (scope.Notifications.PublishCancelable(new ContentDeletingNotification(contents, eventMessages)))
            {
                scope.Complete();
                return;
            }

            // order by level, descending, so deepest first - that way, we cannot move
            // a content of the deleted type, to the recycle bin (and then delete it...)
            foreach (IContent content in contents.OrderByDescending(x => x.ParentId))
            {
                // if it's not trashed yet, and published, we should unpublish
                // but... Unpublishing event makes no sense (not going to cancel?) and no need to save
                // just raise the event
                if (content.Trashed == false && content.Published)
                {
                    scope.Notifications.Publish(new ContentUnpublishedNotification(content, eventMessages));
                }

                // if current content has children, move them to trash
                IContent c = content;
                IQuery<IContent> childQuery = Query<IContent>().Where(x => x.ParentId == c.Id);
                IEnumerable<IContent> children = _documentRepository.Get(childQuery);
                foreach (IContent child in children)
                {
                    // see MoveToRecycleBin
                    PerformMoveLocked(child, Constants.System.RecycleBinContent, null, userId, moves, true);
                    changes.Add(new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch));
                }

                // delete content
                // triggers the deleted event (and handles the files)
                DeleteLocked(scope, content, eventMessages);
                changes.Add(new TreeChange<IContent>(content, TreeChangeTypes.Remove));
            }

            MoveToRecycleBinEventInfo<IContent>[] moveInfos = moves
                .Select(x => new MoveToRecycleBinEventInfo<IContent>(x.Item1, x.Item2))
                .ToArray();
            if (moveInfos.Length > 0)
            {
                scope.Notifications.Publish(new ContentMovedToRecycleBinNotification(moveInfos, eventMessages));
            }

            scope.Notifications.Publish(new ContentTreeChangeNotification(changes, eventMessages));

            Audit(AuditType.Delete, userId, Constants.System.Root, $"Delete content of type {string.Join(",", contentTypeIdsA)}");

            scope.Complete();
        }
    }

    /// <summary>
    ///     Deletes all content items of specified type. All children of deleted content item is moved to Recycle Bin.
    /// </summary>
    /// <remarks>This needs extra care and attention as its potentially a dangerous and extensive operation</remarks>
    /// <param name="contentTypeId">Id of the <see cref="IContentType" /></param>
    /// <param name="userId">Optional id of the user deleting the media</param>
    public void DeleteOfType(int contentTypeId, int userId = Constants.Security.SuperUserId) =>
        DeleteOfTypes(new[] { contentTypeId }, userId);

    #endregion

    #region Blueprints

    public IContent? GetBlueprintById(int id)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            IContent? blueprint = _documentBlueprintRepository.Get(id);
            if (blueprint != null)
            {
                blueprint.Blueprint = true;
            }

            return blueprint;
        }
    }

    public IContent? GetBlueprintById(Guid id)
    {
        using (ICoreScope scope = ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            IContent? blueprint = _documentBlueprintRepository.Get(id);
            if (blueprint != null)
            {
                blueprint.Blueprint = true;
            }

            return blueprint;
        }
    }

    public void SaveBlueprint(IContent content, int userId = Constants.Security.SuperUserId)
        => SaveBlueprint(content, null, userId);

    public void SaveBlueprint(IContent content, IContent? createdFromContent, int userId = Constants.Security.SuperUserId)
    {
        EventMessages evtMsgs = EventMessagesFactory.Get();

        content.Blueprint = true;

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            if (content.HasIdentity == false)
            {
                content.CreatorId = userId;
            }

            content.WriterId = userId;

            _documentBlueprintRepository.Save(content);

            Audit(AuditType.Save, userId, content.Id, $"Saved content template: {content.Name}");

            scope.Notifications.Publish(new ContentSavedBlueprintNotification(content, createdFromContent, evtMsgs));
            scope.Notifications.Publish(new ContentTreeChangeNotification(content, TreeChangeTypes.RefreshNode, evtMsgs));

            scope.Complete();
        }
    }

    public void DeleteBlueprint(IContent content, int userId = Constants.Security.SuperUserId)
    {
        EventMessages evtMsgs = EventMessagesFactory.Get();

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);
            _documentBlueprintRepository.Delete(content);
            scope.Notifications.Publish(new ContentDeletedBlueprintNotification(content, evtMsgs));
            scope.Notifications.Publish(new ContentTreeChangeNotification(content, TreeChangeTypes.Remove, evtMsgs));
            scope.Complete();
        }
    }

    private static readonly string?[] ArrayOfOneNullString = { null };

    public IContent CreateBlueprintFromContent(
        IContent blueprint,
        string name,
        int userId = Constants.Security.SuperUserId)
    {
        ArgumentNullException.ThrowIfNull(blueprint);

        IContentType contentType = GetContentType(blueprint.ContentType.Alias);
        var content = new Content(name, -1, contentType);
        content.Path = string.Concat(content.ParentId.ToString(), ",", content.Id);

        content.CreatorId = userId;
        content.WriterId = userId;

        IEnumerable<string?> cultures = ArrayOfOneNullString;
        if (blueprint.CultureInfos?.Count > 0)
        {
            cultures = blueprint.CultureInfos.Values.Select(x => x.Culture);
            using ICoreScope scope = ScopeProvider.CreateCoreScope();
            if (blueprint.CultureInfos.TryGetValue(_languageRepository.GetDefaultIsoCode(), out ContentCultureInfos defaultCulture))
            {
                defaultCulture.Name = name;
            }

            scope.Complete();
        }

        DateTime now = DateTime.Now;
        foreach (var culture in cultures)
        {
            foreach (IProperty property in blueprint.Properties)
            {
                var propertyCulture = property.PropertyType.VariesByCulture() ? culture : null;
                content.SetValue(property.Alias, property.GetValue(propertyCulture), propertyCulture);
            }

            if (!string.IsNullOrEmpty(culture))
            {
                content.SetCultureInfo(culture, blueprint.GetCultureName(culture), now);
            }
        }

        return content;
    }

    /// <inheritdoc />
    [Obsolete("Use IContentBlueprintEditingService.GetScaffoldedAsync() instead. Scheduled for removal in V18.")]
    public IContent CreateContentFromBlueprint(IContent blueprint, string name, int userId = Constants.Security.SuperUserId)
        => CreateBlueprintFromContent(blueprint, name, userId);

    public IEnumerable<IContent> GetBlueprintsForContentTypes(params int[] contentTypeId)
    {
        using (ScopeProvider.CreateCoreScope(autoComplete: true))
        {
            IQuery<IContent> query = Query<IContent>();
            if (contentTypeId.Length > 0)
            {
                query.Where(x => contentTypeId.Contains(x.ContentTypeId));
            }

            return _documentBlueprintRepository.Get(query).Select(x =>
            {
                x.Blueprint = true;
                return x;
            });
        }
    }

    public void DeleteBlueprintsOfTypes(IEnumerable<int> contentTypeIds, int userId = Constants.Security.SuperUserId)
    {
        EventMessages evtMsgs = EventMessagesFactory.Get();

        using (ICoreScope scope = ScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            var contentTypeIdsA = contentTypeIds.ToArray();
            IQuery<IContent> query = Query<IContent>();
            if (contentTypeIdsA.Length > 0)
            {
                query.Where(x => contentTypeIdsA.Contains(x.ContentTypeId));
            }

            IContent[]? blueprints = _documentBlueprintRepository.Get(query)?.Select(x =>
            {
                x.Blueprint = true;
                return x;
            }).ToArray();

            if (blueprints is not null)
            {
                foreach (IContent blueprint in blueprints)
                {
                    _documentBlueprintRepository.Delete(blueprint);
                }

                scope.Notifications.Publish(new ContentDeletedBlueprintNotification(blueprints, evtMsgs));
                scope.Notifications.Publish(new ContentTreeChangeNotification(blueprints, TreeChangeTypes.Remove, evtMsgs));
                scope.Complete();
            }
        }
    }

    public void DeleteBlueprintsOfType(int contentTypeId, int userId = Constants.Security.SuperUserId) =>
        DeleteBlueprintsOfTypes(new[] { contentTypeId }, userId);

    #endregion

    #region Abstract implementations

    protected override ILogger<ContentService> Logger => _logger;

    protected override int[] ReadLockIds => WriteLockIds;

    protected override int[] WriteLockIds => new[] { Constants.Locks.ContentTree };

    protected override UmbracoObjectTypes ContentObjectType => UmbracoObjectTypes.Document;

    protected override IContent CreateContentInstance(string name, int parentId, IContentType contentType, int userId)
        => new Content(name, parentId, contentType, userId);

    protected override IContent CreateContentInstance(string name, IContent parent, IContentType contentType, int userId)
        => new Content(name, parent, contentType, userId);

    protected override void DeleteLocked(ICoreScope scope, IContent content, EventMessages evtMsgs)
    {
        void DoDelete(IContent c)
        {
            _documentRepository.Delete(c);
            scope.Notifications.Publish(new ContentDeletedNotification(c, evtMsgs));

            // media files deleted by QueuingEventDispatcher
        }

        const int pageSize = 500;
        var total = long.MaxValue;
        while (total > 0)
        {
            // get descendants - ordered from deepest to shallowest
            IEnumerable<IContent> descendants = GetPagedDescendants(content.Id, 0, pageSize, out total, ordering: Ordering.By("Path", Direction.Descending));
            foreach (IContent c in descendants)
            {
                DoDelete(c);
            }
        }

        DoDelete(content);
    }

    protected override SavingNotification<IContent> SavingNotification(IContent content, EventMessages eventMessages)
        => new ContentSavingNotification(content, eventMessages);

    protected override SavedNotification<IContent> SavedNotification(IContent content, EventMessages eventMessages)
        => new ContentSavedNotification(content, eventMessages);

    protected override SavingNotification<IContent> SavingNotification(IEnumerable<IContent> content, EventMessages eventMessages)
        => new ContentSavingNotification(content, eventMessages);

    protected override SavedNotification<IContent> SavedNotification(IEnumerable<IContent> content, EventMessages eventMessages)
        => new ContentSavedNotification(content, eventMessages);

    protected override TreeChangeNotification<IContent> TreeChangeNotification(IContent content, TreeChangeTypes changeTypes, EventMessages eventMessages)
        => new ContentTreeChangeNotification(content, changeTypes, eventMessages);

    protected override TreeChangeNotification<IContent> TreeChangeNotification(IContent content, TreeChangeTypes changeTypes, IEnumerable<string>? publishedCultures, IEnumerable<string>? unpublishedCultures, EventMessages eventMessages)
        => new ContentTreeChangeNotification(content, changeTypes, publishedCultures, unpublishedCultures, eventMessages);

    protected override TreeChangeNotification<IContent> TreeChangeNotification(IEnumerable<IContent> content, TreeChangeTypes changeTypes, EventMessages eventMessages)
        => new ContentTreeChangeNotification(content, changeTypes, eventMessages);

    protected override DeletingNotification<IContent> DeletingNotification(IContent content, EventMessages eventMessages)
        => new ContentDeletingNotification(content, eventMessages);

    protected override CancelableEnumerableObjectNotification<IContent> PublishingNotification(IContent content, EventMessages eventMessages)
        => new ContentPublishingNotification(content, eventMessages);

    protected override IStatefulNotification PublishedNotification(IContent content, EventMessages eventMessages)
        => new ContentPublishedNotification(content, eventMessages);

    protected override IStatefulNotification PublishedNotification(IEnumerable<IContent> content, EventMessages eventMessages)
        => new ContentPublishedNotification(content, eventMessages);

    protected override CancelableEnumerableObjectNotification<IContent> UnpublishingNotification(IContent content, EventMessages eventMessages)
        => new ContentUnpublishingNotification(content, eventMessages);

    protected override IStatefulNotification UnpublishedNotification(IContent content, EventMessages eventMessages)
        => new ContentUnpublishedNotification(content, eventMessages);

    protected override DeletingVersionsNotification<IContent> DeletingVersionsNotification(int id, EventMessages messages, int specificVersion = default, bool deletePriorVersions = false, DateTime dateToRetain = default)
        => new ContentDeletingVersionsNotification(id, messages, specificVersion, deletePriorVersions, dateToRetain);

    protected override DeletedVersionsNotification<IContent> DeletedVersionsNotification(int id, EventMessages messages, int specificVersion = default, bool deletePriorVersions = false, DateTime dateToRetain = default)
        => new ContentDeletedVersionsNotification(id, messages, specificVersion, deletePriorVersions, dateToRetain);

    #endregion
}
