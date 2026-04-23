using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using Octokit;

namespace FanOutFanInCrawler
{
    public static class Orchestrator
    {
        // GitHub token is expected in the "GitHubToken" app setting. See local.settings.json.sample.
        private static readonly Func<string?> getToken = () => Environment.GetEnvironmentVariable("GitHubToken");

        private static readonly GitHubClient github = new GitHubClient(new ProductHeaderValue("FanOutFanInCrawler"))
        {
            Credentials = !string.IsNullOrWhiteSpace(getToken()) ? new Credentials(getToken()) : Credentials.Anonymous
        };

        // Table storage connection string. Defaults to AzureWebJobsStorage (e.g. Azurite) unless overridden.
        private static readonly Func<string?> getStorageConnectionString = () =>
            Environment.GetEnvironmentVariable("StorageConnectionString")
            ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        /// <summary>
        /// HTTP-triggered starter that kicks off the fan-out/fan-in orchestration.
        /// </summary>
        [Function("Orchestrator_HttpStart")]
        public static async Task<HttpResponseData> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext executionContext)
        {
            ILogger logger = executionContext.GetLogger("Orchestrator_HttpStart");

            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(RunOrchestrator), "Nuget");

            logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            return await client.CreateCheckStatusResponseAsync(req, instanceId);
        }

        [Function(nameof(RunOrchestrator))]
        public static async Task<string> RunOrchestrator(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            // Retrieve the organization name passed by the HTTP starter.
            var organizationName = context.GetInput<string>() ?? "Nuget";

            // Fetch the list of repositories for the organization via an activity.
            var repositories = await context.CallActivityAsync<List<RepositoryInfo>>(
                nameof(GetAllRepositoriesForOrganization), organizationName);

            // Fan-out: start a `GetOpenedIssues` activity per repository in parallel.
            var tasks = new Task<RepositoryIssueCount>[repositories.Count];
            for (int i = 0; i < repositories.Count; i++)
            {
                tasks[i] = context.CallActivityAsync<RepositoryIssueCount>(
                    nameof(GetOpenedIssues), repositories[i]);
            }

            // Fan-in: wait for all the parallel activities to finish.
            await Task.WhenAll(tasks);

            var openedIssues = tasks.Select(t => t.Result).ToList();

            // Persist the aggregated results to Table Storage.
            await context.CallActivityAsync(nameof(SaveRepositories), openedIssues);

            return context.InstanceId;
        }

        [Function(nameof(GetAllRepositoriesForOrganization))]
        public static async Task<List<RepositoryInfo>> GetAllRepositoriesForOrganization(
            [ActivityTrigger] string organizationName)
        {
            var repositories = (await github.Repository.GetAllForOrg(organizationName))
                .Select(x => new RepositoryInfo(x.Id, x.Name))
                .ToList();
            return repositories;
        }

        [Function(nameof(GetOpenedIssues))]
        public static async Task<RepositoryIssueCount> GetOpenedIssues(
            [ActivityTrigger] RepositoryInfo repository)
        {
            var issues = (await github.Issue.GetAllForRepository(repository.Id)).ToList();
            int openedIssues = issues.Count(x => x.State == ItemState.Open);
            return new RepositoryIssueCount(repository.Id, openedIssues, repository.Name);
        }

        [Function(nameof(SaveRepositories))]
        public static async Task SaveRepositories(
            [ActivityTrigger] List<RepositoryIssueCount> parameters,
            FunctionContext executionContext)
        {
            ILogger logger = executionContext.GetLogger(nameof(SaveRepositories));

            var connectionString = getStorageConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "No storage connection string is configured. Set 'StorageConnectionString' or 'AzureWebJobsStorage'.");
            }

            var serviceClient = new TableServiceClient(connectionString);
            var tableClient = serviceClient.GetTableClient("Repositories");

            await tableClient.CreateIfNotExistsAsync();

            // Table Storage batch transactions must share a partition key and stay under 100 entities.
            foreach (var chunk in parameters.Chunk(100))
            {
                var batch = chunk
                    .Select(p => new TableTransactionAction(
                        TableTransactionActionType.UpsertMerge,
                        new RepositoryEntity(p.Id)
                        {
                            OpenedIssues = p.OpenedIssues,
                            RepositoryName = p.Name
                        }))
                    .ToList();

                if (batch.Count > 0)
                {
                    await tableClient.SubmitTransactionAsync(batch);
                }
            }

            logger.LogInformation("Saved {count} repositories to Table Storage.", parameters.Count);
        }

        public record RepositoryInfo(long Id, string Name);

        public record RepositoryIssueCount(long Id, int OpenedIssues, string Name);

        public class RepositoryEntity : ITableEntity
        {
            public RepositoryEntity() { }

            public RepositoryEntity(long id)
            {
                PartitionKey = "Default";
                RowKey = id.ToString();
            }

            public string PartitionKey { get; set; } = "Default";
            public string RowKey { get; set; } = string.Empty;
            public DateTimeOffset? Timestamp { get; set; }
            public ETag ETag { get; set; }
            public int OpenedIssues { get; set; }
            public string RepositoryName { get; set; } = string.Empty;
        }
    }
}
