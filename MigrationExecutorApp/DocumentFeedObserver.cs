﻿namespace MigrationConsoleApp
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Azure.CosmosDB.BulkExecutor;
    using Microsoft.Azure.CosmosDB.BulkExecutor.BulkImport;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.ChangeFeedProcessor;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.FeedProcessing;
    using Microsoft.Azure.Documents.Client;
    using ChangeFeedObserverCloseReason = Microsoft.Azure.Documents.ChangeFeedProcessor.FeedProcessing.ChangeFeedObserverCloseReason;
    using IChangeFeedObserver = Microsoft.Azure.Documents.ChangeFeedProcessor.FeedProcessing.IChangeFeedObserver;
    using Azure.Storage.Blobs;
    using Azure.Storage.Blobs.Specialized;

    using Azure.Storage.Blobs.Models;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Text;
    using MongoDB.Bson;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public class DocumentFeedObserver: IChangeFeedObserver
    {
        private readonly DocumentClient client;
        private readonly Uri destinationCollectionUri;
        private IBulkExecutor bulkExecutor;
        private IDocumentTransformer documentTransformer;
        //private AppendBlobClient appendBlobClient;
        private BlobContainerClient containerClient;

        public DocumentFeedObserver(DocumentClient client, DocumentCollectionInfo destCollInfo, IDocumentTransformer documentTransformer, BlobContainerClient containerClient)
        {
            this.client = client;
            this.destinationCollectionUri = UriFactory.CreateDocumentCollectionUri(destCollInfo.DatabaseName, destCollInfo.CollectionName);
            this.documentTransformer = documentTransformer;
            this.containerClient = containerClient;
        }

        public async Task OpenAsync(IChangeFeedObserverContext context)
        {
            client.ConnectionPolicy.RetryOptions.MaxRetryWaitTimeInSeconds = 100000;
            client.ConnectionPolicy.RetryOptions.MaxRetryAttemptsOnThrottledRequests = 100000;

            DocumentCollection destinationCollection = await client.ReadDocumentCollectionAsync(this.destinationCollectionUri);
            bulkExecutor = new BulkExecutor(client, destinationCollection);
            client.ConnectionPolicy.UserAgentSuffix = (" migrationService");

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("Observer opened for partition Key Range: {0}", context.PartitionKeyRangeId);
            Console.ForegroundColor = ConsoleColor.White;

            await bulkExecutor.InitializeAsync();
        }

        public Task CloseAsync(IChangeFeedObserverContext context, ChangeFeedObserverCloseReason reason)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Observer closed, {0}", context.PartitionKeyRangeId);
            Console.WriteLine("Reason for shutdown, {0}", reason);
            Console.ForegroundColor = ConsoleColor.White;

            return Task.CompletedTask;
        }

        public async Task ProcessChangesAsync(
            IChangeFeedObserverContext context, 
            IReadOnlyList<Document> docs, 
            CancellationToken cancellationToken)
        {
            BulkImportResponse bulkImportResponse = new BulkImportResponse();
            try
            {
                List<Document> transformedDocs = new List<Document>();
                foreach(var doc in docs)
                {
                    transformedDocs.AddRange(documentTransformer.TransformDocument(doc).Result);
                }

                bulkImportResponse = await bulkExecutor.BulkImportAsync(
                    documents: transformedDocs,
                    enableUpsert: true,
                    maxConcurrencyPerPartitionKeyRange: 1,
                    disableAutomaticIdGeneration: true,
                    maxInMemorySortingBatchSize: null,
                    cancellationToken: new CancellationToken());

                LogMetrics(context, bulkImportResponse);

                if (bulkImportResponse.FailedImports.Count > 0 && containerClient != null)
                {
                    BlobClient blobClient = containerClient.GetBlobClient("FailedImportDocs" + Guid.NewGuid().ToString() + ".csv");

                    var failedImportDocs = JsonConvert.SerializeObject(String.Join(",", bulkImportResponse.FailedImports.First().DocumentsFailedToImport));

                    byte[] byteArray = Encoding.ASCII.GetBytes(bulkImportResponse.FailedImports.First().BulkImportFailureException.GetType() + "|" + bulkImportResponse.FailedImports.First().DocumentsFailedToImport.Count + "|" + bulkImportResponse.FailedImports.First().BulkImportFailureException.Message.Substring(0, 100) + "|" + failedImportDocs);

                    using (var ms = new MemoryStream(byteArray))
                    {
                        //await appendBlobClient.AppendBlockAsync(ms);
                        await blobClient.UploadAsync(ms, overwrite: true);
                    }
                }

                if (bulkImportResponse.BadInputDocuments.Count > 0 && containerClient != null)
                {
                    BlobClient blobClient = containerClient.GetBlobClient("BadInputDocs" + Guid.NewGuid().ToString() + ".csv");

                    var badInputDocs = JsonConvert.SerializeObject(String.Join(",", bulkImportResponse.BadInputDocuments));

                    byte[] byteArray = Encoding.ASCII.GetBytes("BadInputDocuments, " + bulkImportResponse.BadInputDocuments.Count + "|" + badInputDocs);

                    using (var ms = new MemoryStream(byteArray))
                    {
                        //await appendBlobClient.AppendBlockAsync(ms);
                        await blobClient.UploadAsync(ms, overwrite: true);
                    }
                }

            }
            catch (Exception e)
            {
                Program.telemetryClient.TrackException(e);
            }

            Program.telemetryClient.Flush();
        }

        private static void LogMetrics(IChangeFeedObserverContext context, BulkImportResponse bulkImportResponse)
        {
            //Console.WriteLine("Imported Documents: " + bulkImportResponse.NumberOfDocumentsImported
            //                    + "  by process " + Process.GetCurrentProcess().Id);
            //Console.WriteLine("RUs consumed : " + bulkImportResponse.NumberOfDocumentsImported
            //    + " by process " + Process.GetCurrentProcess().Id);

            Program.telemetryClient.TrackMetric("TotalInserted", bulkImportResponse.NumberOfDocumentsImported);

            Program.telemetryClient.TrackMetric("InsertedDocuments-Process:"
                + Process.GetCurrentProcess().Id, bulkImportResponse.NumberOfDocumentsImported);

            Program.telemetryClient.TrackMetric("TotalRUs", bulkImportResponse.TotalRequestUnitsConsumed);

            if (bulkImportResponse.BadInputDocuments.Count > 0)
            {
                Program.telemetryClient.TrackMetric("BadInputDocsCount", bulkImportResponse.BadInputDocuments.Count);
                //Program.telemetryClient.TrackEvent(String.Join("|", bulkImportResponse.BadInputDocuments));
            }

            if (bulkImportResponse.FailedImports.Count > 0)
            {
                Program.telemetryClient.TrackMetric("FailedImportDocsCount", bulkImportResponse.FailedImports.First().DocumentsFailedToImport.Count);

                //var failedImportDocs = String.Join("|", bulkImportResponse.FailedImports.First().DocumentsFailedToImport);

                //Program.telemetryClient.TrackEvent("Errors encountered in bulk import API execution. Number of failures corresponding to exception of type: "
                //    + bulkImportResponse.FailedImports.First().BulkImportFailureException.Message + " = " + bulkImportResponse.FailedImports.First().DocumentsFailedToImport.Count
                //    + ". The failed import docs are: " + failedImportDocs);
            }
        }
    }
}
