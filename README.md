# Loadstone

**Import data of any shape into a relational database — declaratively.**

Loadstone is an API-first import platform for the messy reality of inbound data: deeply
nested XML from a partner, JSON exports from a SaaS tool, curated CSV files from a
spreadsheet-driven team. You describe a dataset once, in a small JSON manifest — its entity
hierarchy, types, keys, and reference-data rules — and Loadstone turns any of those formats
into clean relational rows: validated, upserted, fully observable, and never silently lost.

[![CI](https://github.com/KadjiProjects/Loadstone/actions/workflows/ci.yml/badge.svg)](https://github.com/KadjiProjects/Loadstone/actions/workflows/ci.yml)
[![License: BSL 1.1](https://img.shields.io/badge/license-BSL%201.1-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

---

## Why Loadstone

Every team eventually writes the same program: parse a file, walk its hierarchy, look up
reference codes, insert parent rows, wire up foreign keys, insert children, handle the bad
rows... and then writes it again for the next feed. Those one-off importers share the same
failure modes: one bad row aborts a two-hour load, "unknown code" means a stack trace, and
when something goes wrong in production nobody can say *which* record, *which* field, or
*why*.

Loadstone is that program, written once, properly:

- **One manifest instead of N importers.** Entities, fields, types, natural keys,
  parent/child hierarchy, lookups, validation — a single declarative JSON document drives
  parsing, validation, staging DDL, and merge SQL for every format.
- **XML, JSON, and CSV in, tables out.** All three readers stream into the same canonical
  record model, so the pipeline neither knows nor cares what the file looked like.
  Multi-gigabyte files import with bounded memory.
- **Hierarchies are first-class.** One table or ten, nested children (optional or
  required) to any depth. Parents are merged first; their database keys flow to the
  children automatically. No triggers, no ORMs, no per-row round trips — bulk staging and
  set-based merges all the way down.
- **Bad rows don't kill good files.** Records that fail validation or reference-data
  resolution are diverted to a rejection store with the source line, field, raw value, and
  reason. The rest of the file imports. You fix the six broken rows, not the whole feed.
- **Reference data is pluggable.** The built-in code-list tables cover the classic
  "string code to id" case with per-field policies (reject the record, reject the file,
  fall back to a default, or auto-create). Anything else — a REST API, another database,
  a static dictionary — is one small `ILookupProvider` implementation away.
- **A durable queue per dataset.** Imports are jobs on named, database-backed queues:
  they survive restarts, retry with exponential backoff, dead-letter when exhausted, and
  scale out safely across multiple workers with no extra infrastructure.
- **Observability is not an afterthought.** Structured logs (Serilog), traces and metrics
  (OpenTelemetry), a correlation id that follows every job end to end, per-job timelines,
  and queryable rejection reports — over HTTP, not just in log files.

## Quick start

The fastest route is Docker (SQL Server included):

```bash
git clone https://github.com/KadjiProjects/Loadstone.git
cd Loadstone
docker compose up --build
```

Open http://localhost:8080 — the operations dashboard, with the sample **orders**
dataset registered and its tables created automatically (the raw API lives at
`/swagger`). Import the sample file from the dashboard's upload form, or:

```bash
curl -X POST "http://localhost:8080/api/datasets/orders/imports" \
     -F "file=@samples/orders/orders.xml"
```

The response contains a job id. Watch it complete:

```bash
curl "http://localhost:8080/api/imports/<jobId>"
curl "http://localhost:8080/api/imports/<jobId>/events"
curl "http://localhost:8080/api/imports/<jobId>/rejections"
```

The same dataset also accepts JSON (`samples/orders/orders.json`) and hierarchical CSV
(zip `samples/orders/csv/*.csv` and upload the archive). Same manifest, same tables,
three formats.

Running without Docker: install the [.NET 10 SDK](https://dotnet.microsoft.com/download),
point `Loadstone:ConnectionString` (or the `ConnectionStrings:Loadstone` entry) at any SQL
Server, and `dotnet run --project src/Loadstone.Api`.

## Describing a dataset

A manifest is the entire definition of an import. This is the sample shipped in
`samples/orders`:

```json
{
  "name": "orders",
  "description": "Customer orders with their lines",
  "root": {
    "name": "Order",
    "table": "Orders",
    "keyColumn": "OrderId",
    "naturalKey": [ "OrderNumber" ],
    "fields": [
      { "name": "OrderNumber", "required": true, "maxLength": 20 },
      { "name": "OrderDate", "type": "date" },
      { "name": "Country", "lookup": { "list": "countries", "onMissing": "autoCreate" } },
      { "name": "Total", "type": "decimal" }
    ],
    "children": [
      {
        "name": "Line",
        "table": "OrderLines",
        "keyColumn": "LineId",
        "parentKeyColumn": "OrderId",
        "naturalKey": [ "LineNumber" ],
        "fields": [
          { "name": "LineNumber", "type": "int32", "required": true },
          { "name": "Sku" },
          { "name": "Quantity", "type": "int32" }
        ]
      }
    ]
  }
}
```

From this one document Loadstone derives:

- what to read from XML elements/attributes, JSON properties, or CSV columns;
- type conversion and validation (required, max length, formats, invariant parsing);
- how `Country` strings resolve to code-list ids, and what to do with unknown ones;
- the staging tables, the merge statements (upsert on `OrderNumber`; lines upsert on
  `OrderId + LineNumber`), and the foreign-key wiring;
- the target-table DDL (`GET /api/datasets/orders/schema`), if you want Loadstone to
  create the schema for you;
- a dedicated queue named `orders`, with retry and dead-letter behavior.

Register or update a dataset at runtime with `PUT /api/datasets/{name}`, or drop manifest
files into `data/datasets/` and version them with your app. The full reference lives in
[docs/manifest-reference.md](docs/manifest-reference.md).

## Extending the pipeline

Imports run through an ordered chain of steps: parse → validate → resolve lookups → your
steps → bulk write. Both extension points are ordinary DI registrations:

```csharp
builder.Services.AddLoadstone()
    .UseSqlServer()
    .AddSourceReader<XmlSourceReader>()
    .AddSourceReader<JsonSourceReader>()
    .AddSourceReader<CsvSourceReader>()
    .AddStep<GeocodeAddressesStep>()          // custom IImportStep
    .AddLookupProvider<CrmCustomerLookup>();  // custom ILookupProvider
```

A step sees every record tree before it is written and can enrich values, apply
cross-field rules, or mark records as errors (which routes them to the rejection store
instead of the database). A lookup provider answers "what does this raw value mean?" from
any source you like; caching and missing-value policies are handled for you.

Datasets can also be declared in code and checked at compile time:

```csharp
public sealed class OrdersDataset : IDataset
{
    public static DatasetManifest Manifest { get; } = new() { /* ... */ };
}

builder.Services.AddLoadstone().AddDataset<OrdersDataset>();
```

## Observability

- **Logs** — structured Serilog output with the job id, dataset, and correlation id on
  every line; ship them anywhere Serilog can (console by default).
- **Traces & metrics** — Loadstone emits an OpenTelemetry `ActivitySource` and `Meter`
  (both named `Loadstone`): job spans tagged with dataset/outcome/attempt, plus counters
  for records read, records rejected, and rows written, and a job-duration histogram.
  Set `OTEL_EXPORTER_OTLP_ENDPOINT` and everything flows to your collector — Azure
  Monitor, Grafana, Jaeger, Datadog, take your pick.
- **The API itself** — `/api/imports` for job state and counts, `/{id}/events` for the
  stage timeline, `/{id}/rejections` for row-level failures with source locations, and
  `/health` for probes.
- **The dashboard** — a zero-dependency operations UI at `/`: live job table, per-job
  timeline and rejection browser, dataset manifests and generated schema, and code-list
  management.

## Running on Azure

Loadstone is a single ASP.NET Core app with background workers hosted in-process, which
is exactly the shape Azure App Service wants:

1. Create an App Service (or container app) from the provided Dockerfile, and an Azure
   SQL database.
2. Set `Loadstone__ConnectionString` (Key Vault reference recommended).
3. Point `Loadstone__FileStorePath` and `Loadstone__ManifestDirectory` at the persistent
   `%HOME%\data` share, and enable *Always On* so queue workers keep polling.
4. Optional: set `OTEL_EXPORTER_OTLP_ENDPOINT` to light up Application Insights via the
   Azure Monitor OTLP ingestion.

No code changes — the queue is SQL-backed, file storage and manifests are directory-based
abstractions, and configuration is standard `IConfiguration` (environment variables, Key
Vault, App Configuration all work as-is). Swapping the file store for Blob Storage or the
queue for Service Bus later is implementing one small interface, not a rewrite.

## Validating before import

Loadstone pairs naturally with [Rowvane Gate](https://github.com/KadjiProjects/RowvaneGate),
the declarative file-validation engine: Gate bounces whole bad files with a forensic
report *before* they consume an import job; Loadstone quarantines the row-level
stragglers its lookups and conversions catch during import. The
[validate &amp; import guide](docs/validate-and-import-guide.md) covers all three modes —
validation alone, import alone, or both chained into one pipeline — including how to
keep a Gate ruleset and a Loadstone manifest in sync for the same feed.

## Project layout

| Project | What it is |
| --- | --- |
| `Loadstone.Core` | Contracts and the manifest/record model. No dependencies. |
| `Loadstone.Readers` | Streaming XML, JSON, and CSV readers (including zip-of-CSVs hierarchies). |
| `Loadstone.Runtime` | Pipeline engine, built-in steps, lookup resolution, queue workers, DI. |
| `Loadstone.SqlServer` | Durable queue, job store, code lists, schema generation, bulk staging/merge writer. |
| `Loadstone.Api` | The HTTP host: REST API, Swagger, health checks, Serilog + OpenTelemetry. |
| `Loadstone.Tests` | Unit tests for the manifest model, readers, pipeline, and SQL generation. |

## Roadmap

- PostgreSQL provider (`COPY` + `INSERT ... ON CONFLICT`) behind the existing writer contract
- Azure Blob file store and Service Bus queue transport
- Dataset-level webhooks (job completed / dead-lettered)
- Published benchmarks
- Parquet reader

Contributions toward any of these (or a good case for something else) are very welcome —
see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Loadstone is source-available under the [Business Source License 1.1](LICENSE):
**free for self-hosted production use at any scale, commercial or not.** Each release
converts to Apache 2.0 four years after it ships. The two things that require a
[commercial license](COMMERCIAL-LICENSE.md) are offering Loadstone as a hosted service
to third parties and embedding it in a product you sell. Enterprise support plans and
OEM licenses are available — see [COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md).
