﻿using CosmosManager.Domain;
using CosmosManager.Extensions;
using CosmosManager.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace CosmosManager.QueryRunners
{
    public class UpdateByWhereQueryRunner : IQueryRunner
    {
        private int MAX_DEGREE_PARALLEL = 5;
        private IQueryStatementParser _queryParser;
        private readonly ITransactionTask _transactionTask;
        private readonly IVariableInjectionTask _variableInjectionTask;

        public UpdateByWhereQueryRunner(ITransactionTask transactionTask, IQueryStatementParser queryStatementParser, IVariableInjectionTask variableInjectionTask)
        {
            _queryParser = queryStatementParser;
            _transactionTask = transactionTask;
            _variableInjectionTask = variableInjectionTask;
        }

        public bool CanRun(QueryParts queryParts)
        {
            return queryParts.CleanQueryType.Equals(Constants.QueryParsingKeywords.UPDATE, StringComparison.InvariantCultureIgnoreCase)
                && queryParts.CleanQueryBody.Equals("*")
                && !string.IsNullOrEmpty(queryParts.CleanQueryUpdateType)
                && !string.IsNullOrEmpty(queryParts.CleanQueryUpdateBody)
                && !string.IsNullOrEmpty(queryParts.CleanQueryWhere);
        }

        public async Task<(bool success, IReadOnlyCollection<object> results)> RunAsync(IDocumentStore documentStore, Connection connection, QueryParts queryParts, bool logStats, ILogger logger, CancellationToken cancellationToken, Dictionary<string, IReadOnlyCollection<object>> variables = null)
        {
            try
            {
                if (!queryParts.IsValidQuery())
                {
                    logger.LogError("Invalid Query. Aborting Update.");
                    return (false, null);
                }

                if (queryParts.IsReplaceUpdateQuery())
                {
                    logger.LogError($"Full document updating not supported in SELECT/WHERE queries. To update those documents use an update by documentId query. Skipping Update.");
                    return (false, null);
                }
                //get the ids
                var selectQuery = queryParts.ToRawSelectQuery();
                var results = await documentStore.ExecuteAsync(connection.Database, queryParts.CollectionName,
                                                                      async (IDocumentExecuteContext context) =>
                                                                     {
                                                                         var queryOptions = new QueryOptions
                                                                         {
                                                                             PopulateQueryMetrics = true,
                                                                             EnableCrossPartitionQuery = true,
                                                                             MaxBufferedItemCount = 200,
                                                                             MaxDegreeOfParallelism = MAX_DEGREE_PARALLEL,
                                                                             MaxItemCount = -1,
                                                                         };

                                                                         if (variables != null && variables.Any() && queryParts.HasVariablesInWhereClause())
                                                                         {
                                                                             selectQuery = _variableInjectionTask.InjectVariables(selectQuery, variables, logger);
                                                                         }

                                                                         var query = context.QueryAsSql<object>(selectQuery, queryOptions);
                                                                         return await query.ConvertAndLogRequestUnits(false, logger);
                                                                     }, cancellationToken);

                var fromObjects = JArray.FromObject(results);
                if (queryParts.IsTransaction)
                {
                    logger.LogInformation($"Transaction Created. TransactionId: {queryParts.TransactionId}");
                    await _transactionTask.BackuQueryAsync(connection.Name, connection.Database, queryParts.CollectionName, queryParts.TransactionId, queryParts.CleanOrginalQuery);
                }
                var partitionKeyPath = await documentStore.LookupPartitionKeyPath(connection.Database, queryParts.CollectionName);

                var updateCount = 0;
                var actionTransactionCacheBlock = new ActionBlock<JObject>(async document =>
                                                                       {
                                                                           await documentStore.ExecuteAsync(connection.Database, queryParts.CollectionName,
                                                                                        async (IDocumentExecuteContext context) =>
                                                                                        {
                                                                                            if (cancellationToken.IsCancellationRequested)
                                                                                            {
                                                                                                throw new TaskCanceledException("Task has been requested to cancel.");
                                                                                            }
                                                                                            var documentId = document[Constants.DocumentFields.ID].ToString();

                                                                                            if (queryParts.IsTransaction)
                                                                                            {
                                                                                                var backupResult = await _transactionTask.BackupAsync(context, connection.Name, connection.Database, queryParts.CollectionName, queryParts.TransactionId, logger, null, document);
                                                                                                if (!backupResult.isSuccess)
                                                                                                {
                                                                                                    logger.LogError($"Unable to backup document {documentId}. Skipping Update.");
                                                                                                    return false;
                                                                                                }
                                                                                            }

                                                                                            var partionKeyValue = document.SelectToken(partitionKeyPath).ToString();

                                                                                            var partialDoc = JObject.Parse(queryParts.CleanQueryUpdateBody);
                                                                                            //ensure the partial update is not trying to update id or the partition key
                                                                                            var pToken = partialDoc.SelectToken(partitionKeyPath);
                                                                                            var idToken = partialDoc.SelectToken(Constants.DocumentFields.ID);
                                                                                            if (pToken != null || idToken != null)
                                                                                            {
                                                                                                logger.LogError($"Updates are not allowed on ids or existing partition keys of a document. Skipping updated for document {documentId}.");
                                                                                                return false;
                                                                                            }
                                                                                            var shouldUpdateToEmptyArray = partialDoc.HasEmptyJArray();
                                                                                            document.Merge(partialDoc, new JsonMergeSettings
                                                                                            {
                                                                                                MergeArrayHandling = shouldUpdateToEmptyArray ? MergeArrayHandling.Replace : MergeArrayHandling.Merge,
                                                                                                MergeNullValueHandling = MergeNullValueHandling.Merge
                                                                                            });

                                                                                            //save
                                                                                            var updatedDoc = await context.UpdateAsync(document, new RequestOptions
                                                                                            {
                                                                                                PartitionKey = partionKeyValue
                                                                                            });
                                                                                            if (updatedDoc != null)
                                                                                            {
                                                                                                Interlocked.Increment(ref updateCount);
                                                                                                logger.LogInformation($"Updated {documentId}");
                                                                                            }
                                                                                            else
                                                                                            {
                                                                                                logger.LogInformation($"Document {documentId} unable to be updated.");
                                                                                            }
                                                                                            return true;
                                                                                        }, cancellationToken);
                                                                       },
                                                                       new ExecutionDataflowBlockOptions
                                                                       {
                                                                           MaxDegreeOfParallelism = MAX_DEGREE_PARALLEL,
                                                                           CancellationToken = cancellationToken
                                                                       });

                foreach (JObject doc in fromObjects)
                {
                    actionTransactionCacheBlock.Post(doc);
                }
                actionTransactionCacheBlock.Complete();
                await actionTransactionCacheBlock.Completion;
                logger.LogInformation($"Updated {updateCount} out of {fromObjects.Count}");
                if (queryParts.IsTransaction && updateCount > 0)
                {
                    logger.LogInformation($"To rollback execute: ROLLBACK {queryParts.TransactionId}");
                }
                return (true, null);
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, new EventId(), $"Unable to run {Constants.QueryParsingKeywords.DELETE} query", ex);
                return (false, null);
            }
        }
    }
}