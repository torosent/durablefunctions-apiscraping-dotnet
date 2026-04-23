---
page_type: sample
products:
- azure
- dotnet
- azure-functions
- azure-durable-task-scheduler
languages:
- csharp
extensions:
  ms.author: marouill
  ms.custom: nextgen
name: "Retrieve opened issue count on GitHub with Azure Durable Functions (.NET)"
urlFragment: retrieve-opened-issue-count-on-github-with-azure-durable-functions
description: "Build an Azure Durable Functions app that scrapes GitHub for opened issues using the Azure Durable Task Scheduler backend."
---

# Retrieve opened issue count on GitHub with Azure Durable Functions

This sample uses the [fan-out / fan-in](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-cloud-backup) Durable Functions pattern to scrape a GitHub organisation for the number of open issues per repository and persist the result to Azure Table Storage.

> **State is managed by [Azure Durable Task Scheduler (DTS)](https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler).**  The project was migrated from the classic Azure Storage backend (in-process worker) to the **isolated worker model** with the `azureManaged` storage provider. Orchestration history, instances, and queues now live in a managed DTS task hub.

## Prerequisites

* [.NET 8 SDK](https://dotnet.microsoft.com/download)
* [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
* [Docker Desktop](https://www.docker.com/products/docker-desktop/) (to run the DTS emulator and Azurite locally)
* [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
* Azure subscription ([free trial](https://azure.microsoft.com/free/))
* GitHub personal access token with `public_repo` scope ([docs](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens))

## Run locally with the DTS emulator

1. **Start the DTS emulator**

   ```bash
   docker run --rm -p 8080:8080 -p 8082:8082 mcr.microsoft.com/dts/dts-emulator:latest
   ```

   Port `8080` hosts the task hub gRPC endpoint; port `8082` serves the dashboard at <http://localhost:8082>.

2. **Start Azurite** for the storage account used to persist the scraped results.

   ```bash
   docker run --rm -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite
   ```

3. **Create `FanOutFanInCrawler/local.settings.json`** (copy from the provided sample):

   ```bash
   cp FanOutFanInCrawler/local.settings.json.sample FanOutFanInCrawler/local.settings.json
   ```

   Then update `GitHubToken` with your PAT. The relevant settings are:

   ```json
   {
     "IsEncrypted": false,
     "Values": {
       "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
       "AzureWebJobsStorage": "UseDevelopmentStorage=true",
       "DURABLE_TASK_SCHEDULER_CONNECTION_STRING": "Endpoint=http://localhost:8080;Authentication=None",
       "TASKHUB_NAME": "default",
       "StorageConnectionString": "UseDevelopmentStorage=true",
       "GitHubToken": "INSERT_TOKEN_HERE"
     }
   }
   ```

4. **Build and start the Function app**

   ```bash
   cd FanOutFanInCrawler
   dotnet build
   func start
   ```

5. **Trigger the orchestration** by sending an HTTP request to the starter function:

   ```bash
   curl http://localhost:7071/api/Orchestrator_HttpStart
   ```

   Use the returned `statusQueryGetUri` to poll the run. Open the emulator dashboard at <http://localhost:8082> to watch the orchestration progress through the task hub.

## Deploy to Azure

The repo ships with an `azure.yaml` and `infra/` Bicep modules that provision a DTS scheduler + task hub, a Flex Consumption Function App, storage, a user-assigned managed identity, Log Analytics, and Application Insights.

```bash
azd auth login
azd up
```

Default region is **`northcentralus`**; other allowed locations are listed in `infra/main.bicep`.

After deployment:

* Discover the function app and DTS task hub with `azd show`.
* Trigger the orchestration:

  ```bash
  curl "https://<function-app>.azurewebsites.net/api/Orchestrator_HttpStart"
  ```

* Monitor orchestration runs through the DTS portal dashboard or via the hosted DTS dashboard at <https://dashboard.durabletask.io>.

### What's in `infra/`

| File | Purpose |
|---|---|
| `infra/main.bicep` | Subscription-scoped entry point; wires UAMI, storage (blob only), Flex Consumption plan, Log Analytics, Application Insights, DTS + task hub, and role assignments |
| `infra/app/api.bicep` | Flex Consumption Function App with `DURABLE_TASK_SCHEDULER_CONNECTION_STRING` and `TASKHUB_NAME` app settings |
| `infra/app/dts.bicep` | `Microsoft.DurableTask/schedulers` + `taskHubs` child resource |
| `infra/app/dts-Access.bicep` | Grants the `Durable Task Data Contributor` role (`0ad04412-c4d5-4796-b79c-f76d14c8d402`) to the managed identity and deployer |
| `infra/app/rbac.bicep` | Storage Blob Data Owner + Monitoring Metrics Publisher role assignments |

## Migrating from the pre-DTS version

The pre-migration commit is tagged [`pre-dts-azure-storage`](https://github.com/Azure-Samples/durablefunctions-apiscraping-dotnet/tree/pre-dts-azure-storage) on the fork used for this migration. Diffs to be aware of when upgrading your own branch:

* `host.json` now declares `storageProvider.type = "azureManaged"` and references `DURABLE_TASK_SCHEDULER_CONNECTION_STRING` / `TASKHUB_NAME` app settings.
* The project was converted from the in-process worker (`Microsoft.NET.Sdk.Functions`, `[FunctionName]`) to the **isolated worker** (`Microsoft.Azure.Functions.Worker.*`, `[Function]`, `TaskOrchestrationContext`, `DurableTaskClient`).
* `Microsoft.WindowsAzure.Storage` was replaced with `Azure.Data.Tables` (the legacy SDK does not support .NET 8+).
* Queue/Table RBAC role assignments were removed — DTS does not use them.

## Resources

* [Azure Durable Task Scheduler documentation](https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler)
* [Azure Functions isolated worker model](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide)
* [Durable Functions overview](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-overview)
* Durable Functions patterns used in this sample
  * [Chaining](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-sequence)
  * [Fan-out / fan-in](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-cloud-backup)
