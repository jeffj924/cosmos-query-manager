using System;
using System.CodeDom;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Doobry.Infrastructure;
using MaterialDesignThemes.Wpf;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;

namespace Doobry.Features.Management
{
    public class ManagementActionsController : IManagementActionsController
    {
        private readonly IDialogTargetFinder _dialogTargetFinder;
        private readonly ISnackbarMessageQueue _snackbarMessageQueue;

        public ManagementActionsController(IDialogTargetFinder dialogTargetFinder, ISnackbarMessageQueue snackbarMessageQueue)
        {
            if (dialogTargetFinder == null) throw new ArgumentNullException(nameof(dialogTargetFinder));
            if (snackbarMessageQueue == null) throw new ArgumentNullException(nameof(snackbarMessageQueue));

            _dialogTargetFinder = dialogTargetFinder;
            _snackbarMessageQueue = snackbarMessageQueue;
        }

        public async Task<ManagementActionAddResult> AddDatabase(string host, string authorisationKey)
        {
            var createItemProperties = new CreateItemProperties("Database");
            return await DoAction(
                createItemProperties,
                properties => DoAddDatabase(properties, host, authorisationKey),
                (result, properties) => result ? ManagementActionAddResult.Complete(properties.ItemId) : ManagementActionAddResult.Incomplete());
        }

        public async Task<ManagementActionAddResult> AddDatabase(HostNode host)
        {
            return await AddDatabase(host.Host, host.AuthorisationKey);
        }

        public async Task<ManagementActionAddResult> AddCollection(string host, string authorisationKey, string databaseId)
        {
            var createItemProperties = new CreateItemProperties("Collection");
            return await DoAction(
                createItemProperties,
                properties => DoAddCollection(properties, host, authorisationKey, databaseId),
                (result, properties) => result ? ManagementActionAddResult.Complete(properties.ItemId) : ManagementActionAddResult.Incomplete());
        }

        public async Task<ManagementActionAddResult> AddCollection(DatabaseNode database)
        {
            return await AddCollection(database.Owner.Host, database.Owner.AuthorisationKey, database.DatabaseId);
        }

        public async Task<bool> DeleteDatabase(string host, string authorisationKey, string databaseId)
        {
            var deleteDatabaseProperties = new DeleteItemProperties("database", databaseId);
            return await DoAction(
                deleteDatabaseProperties,
                p => DoDeleteDatabase(host, authorisationKey, databaseId));
        }

        public async Task<bool> DeleteDatabase(DatabaseNode database)
        {
            return await DeleteDatabase(database.Owner.Host, database.Owner.AuthorisationKey, database.DatabaseId);
        }

        public async Task<bool> DeleteCollection(string host, string authorisationKey, string databaseId, string collectionId)
        {
            var createItemProperties = new DeleteItemProperties("Collection", collectionId);
            return await DoAction(
                createItemProperties,
                properties => DoDeleteCollection(host, authorisationKey, databaseId, collectionId));
        }

        public async Task<bool> DeleteCollection(CollectionNode collection)
        {
            return
                await
                    DeleteCollection(collection.Owner.Owner.Host, collection.Owner.Owner.AuthorisationKey,
                        collection.Owner.DatabaseId, collection.CollectionId);
        }

        private async Task<bool> DoAction<TProperties>(TProperties properties, Func<TProperties, Task> taskFactory)
            where TProperties : INotifyPropertyChanged, INotifyDataErrorInfo
        {
            return await DoAction(properties, taskFactory, (r, p) => r);
        }

        private async Task<TResult> DoAction<TProperties, TResult>(TProperties properties, Func<TProperties, Task> taskFactory, Func<bool, TProperties, TResult> resultFormatter) 
            where TProperties : INotifyPropertyChanged, INotifyDataErrorInfo
        {
            var view = new ManagementAction();
            
            ManagementActionViewModel<TProperties> model = null;
            var result = (bool) await DialogHost.Show(view, _dialogTargetFinder.SuggestDialogHostIdentifier(),
                delegate (object sender, DialogOpenedEventArgs args)
                {
                    model = new ManagementActionViewModel<TProperties>(properties, taskFactory, res => args.Session.Close(res));
                    view.DataContext = model;
                });
            model?.Dispose();

            return resultFormatter(result, properties);
        }

        private static Task DoAddCollection(CreateItemProperties properties,
            string host, string authorisationKey, string databaseId)
        {
            var documentClient = CreateDocumentClient(host, authorisationKey);
            var documentCollection = new DocumentCollection
            {
                Id = properties.ItemId
            };
            return documentClient.CreateDocumentCollectionAsync("dbs/" + databaseId, documentCollection);
        }

        private static Task DoDeleteCollection(string host, string authorisationKey, string databaseId, string collectionId)
        {
            var documentClient = CreateDocumentClient(host, authorisationKey);
            return documentClient.DeleteDocumentCollectionAsync(
                $"dbs/{databaseId}/colls/{collectionId}");
        }

        private static Task DoDeleteDatabase(string host, string authorisationKey, string databaseId)
        {
            return CreateDocumentClient(host, authorisationKey).DeleteDatabaseAsync("dbs/" + databaseId);
        }

        private static Task DoAddDatabase(CreateItemProperties properties, string host, string authorisationKey)
        {
            var database = new Database
            {
                Id = properties.ItemId
            };
            return CreateDocumentClient(host, authorisationKey).CreateDatabaseAsync(database);
        }

        private static DocumentClient CreateDocumentClient(string host, string authorisationKey)
        {
            return new DocumentClient(new Uri(host), authorisationKey, new ConnectionPolicy
            {
                RequestTimeout = TimeSpan.FromSeconds(5)
            });
        }
    }
}