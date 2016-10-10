using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Doobry.Infrastructure;
using Doobry.Settings;
using MaterialDesignThemes.Wpf;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;

namespace Doobry
{
    public class QueryRunnerViewModel : INotifyPropertyChanged
    {
        private readonly Func<Connection> _connectionProvider;
        private readonly Func<GeneralSettings> _generalSettingsProvider;
        private readonly Action<Result> _editHandler;
        private readonly Command _fetchMoreCommand;
        private string _query;
        private TextBox _textBox;
        private Tuple<DocumentClient, IDocumentQuery<dynamic>> _activeDocumentQuery;

        public QueryRunnerViewModel(
            Func<Connection> connectionProvider,
            Func<GeneralSettings> generalSettingsProvider,
            Action<Result> editHandler)
        {
            if (connectionProvider == null) throw new ArgumentNullException(nameof(connectionProvider));
            if (generalSettingsProvider == null) throw new ArgumentNullException(nameof(generalSettingsProvider));
            if (editHandler == null) throw new ArgumentNullException(nameof(editHandler));

            _connectionProvider = connectionProvider;
            _generalSettingsProvider = generalSettingsProvider;
            _editHandler = editHandler;
            RunQueryCommand = new Command(_ => RunQuery());
            _fetchMoreCommand = new Command(_ => FetchMore(), _ => _activeDocumentQuery != null && _activeDocumentQuery.Item2.HasMoreResults);

            var editDocumentCommand = new Command(o => _editHandler((Result)o), o => o is Result);
            var deleteDocumentCommand = new Command(o => DeleteDocument((Result)o), o => o is Result);

            ResultSetExplorer = new ResultSetExplorerViewModel(_fetchMoreCommand, editDocumentCommand, deleteDocumentCommand);
        }

        public static readonly DependencyProperty SelfProperty = DependencyProperty.RegisterAttached(
            "Self", typeof(QueryRunnerViewModel), typeof(QueryRunnerViewModel), new PropertyMetadata(default(QueryRunnerViewModel), SelfPropertyChangedCallback));

        private static void SelfPropertyChangedCallback(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs dependencyPropertyChangedEventArgs)
        {
            (dependencyPropertyChangedEventArgs.NewValue as QueryRunnerViewModel)?.Receive(dependencyObject as TextBox);
        }

        public static void SetSelf(DependencyObject element, QueryRunnerViewModel value)
        {
            element.SetValue(SelfProperty, value);
        }

        public static QueryRunnerViewModel GetSelf(DependencyObject element)
        {
            return (QueryRunnerViewModel)element.GetValue(SelfProperty);
        }

        internal void Receive(TextBox textBox)
        {
            _textBox = textBox;
        }

        public string Query
        {
            get { return _query; }
            set { this.MutateVerbose(ref _query, value, RaisePropertyChanged()); }
        }

        public void Run(string query)
        {
            RunQueryAsync(query);
        }

        public ICommand RunQueryCommand { get; }

        public ResultSetExplorerViewModel ResultSetExplorer { get; }

        private void RunQuery()
        {
            var query =
                _textBox != null && _textBox.SelectionLength > 0
                    ? _textBox.SelectedText
                    : Query;

            RunQueryAsync(query);
        }

        private async void RunQueryAsync(string query)
        {
            var connection = _connectionProvider();

            //TODO disable commands
            if (query == null || connection == null) return;

            var source = new CancellationTokenSource();
            var waitHandle = new ManualResetEvent(false);

            source.Token.Register(() => waitHandle.Set());

            var cancellableDialogViewModel = new CancellableDialogViewModel(() => source.Cancel(), TimeSpan.FromMilliseconds(2500),
                Dispatcher.CurrentDispatcher);
            var cancellableDialog = new CancellableDialog
            {
                DataContext = cancellableDialogViewModel
            };

            await DialogHost.Show(cancellableDialog, delegate (object sender, DialogOpenedEventArgs args)
            {
                RunQuery(connection, _generalSettingsProvider().MaxItemCount, query, waitHandle, source, args);
            });
        }

        private async void RunQuery(Connection connection, int? maxItemCount, string query, EventWaitHandle waitHandle, CancellationTokenSource source,
            DialogOpenedEventArgs args)
        {
            ResultSetExplorer.SelectedRow = -1;
            ResultSetExplorer.ResultSet = null;

            await Task<ResultSet>.Factory
                .StartNew(() =>
                {
                    ResultSet resultSet = null;

                    Task.Factory.StartNew(async () =>
                    {
                        resultSet = await RunQuery(connection, maxItemCount, query);
                        waitHandle.Set();
                    }, source.Token);

                    waitHandle.WaitOne();
                    source.Token.ThrowIfCancellationRequested();

                    return resultSet;
                }, source.Token)
                .ContinueWith(task =>
                {
                    ResultSetExplorer.ResultSet = task.IsCanceled ? new ResultSet("Cancelled") : task.Result;
                    args.Session.Close();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }


        private async Task<ResultSet> RunQuery(Connection connection, int? maxItemCount, string query)
        {
            try
            {
                _activeDocumentQuery?.Item1.Dispose();

                var documentClient = CreateDocumentClient(connection);
                var feedOptions = new FeedOptions { MaxItemCount = maxItemCount };
                var documentQuery = documentClient.CreateDocumentQuery(
                    UriFactory.CreateDocumentCollectionUri(connection.DatabaseId, connection.CollectionId), query,
                    feedOptions).AsDocumentQuery();

                _activeDocumentQuery = new Tuple<DocumentClient, IDocumentQuery<dynamic>>(documentClient, documentQuery);

                var results =
                    (await documentQuery.ExecuteNextAsync()).Select((dy, row) => new Result(row, dy.ToString()));

                _fetchMoreCommand.Refresh();

                return new ResultSet(results);

            }
            catch (DocumentClientException de)
            {
                return new ResultSet(de);
            }
            catch (AggregateException ae)
            {
                var documentClientException = ae.Flatten().InnerExceptions.First() as DocumentClientException;
                return documentClientException != null
                    ? new ResultSet(documentClientException)
                    : new ResultSet(ae.Message);
            }
            catch (Exception e)
            {
                var baseException = e.GetBaseException();
                return new ResultSet(baseException.Message);
            }
        }

        private static DocumentClient CreateDocumentClient(Connection connection)
        {
            return new DocumentClient(new Uri(connection.Host), connection.AuthorisationKey);
        }

        private async void FetchMore()
        {
            await FetchNextUnloadedResults(ResultSetExplorer.ResultSet);
        }

        private async Task FetchNextUnloadedResults(ResultSet intoResultSet)
        {
            if (!_activeDocumentQuery.Item2.HasMoreResults) return;

            var results = (await _activeDocumentQuery.Item2.ExecuteNextAsync()).Select(dy => (string)dy.ToString()).ToList();

            intoResultSet.Append(results);
            _fetchMoreCommand.Refresh();
        }

        private async void DeleteDocument(Result result)
        {
            var dialogContentControl = new DialogContentControl
            {
                Padding = new Thickness(16),
                Title = "Confirm Delete",
                Content = $"Are you sure you wish to delete this document?{Environment.NewLine + Environment.NewLine}id: {result.Id.Raw + Environment.NewLine}_self: {result.Id.Self}"
            };
            var confirmation = await DialogHost.Show(dialogContentControl);
            if (!bool.TrueString.Equals(confirmation)) return;

            var progressRing = new ProgressRing();
            await DialogHost.Show(progressRing, delegate (object sender, DialogOpenedEventArgs args)
            {
                Task.Factory.StartNew(async () =>
                {
                    using (var client = CreateDocumentClient(_connectionProvider()))
                    {
                        await client.DeleteDocumentAsync(result.Id.Self);
                    }
                }).ContinueWith(task =>
                {
                    if (task.Exception != null)
                    {
                        args.Session.UpdateContent(new MessageDialog
                        {
                            Title = "Delete Error",
                            Content = task.Exception.Flatten().Message
                        });
                    }
                    else
                    {
                        args.Session.Close();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            });


        }

        public event PropertyChangedEventHandler PropertyChanged;

        private Action<PropertyChangedEventArgs> RaisePropertyChanged()
        {
            return args => PropertyChanged?.Invoke(this, args);
        }
    }
}
