# Dataset manifest reference

A dataset manifest is a JSON document that fully describes one import: the entity
hierarchy, field types and rules, reference-data lookups, and queue behavior. Manifests
are validated on registration; an invalid manifest is rejected with every problem listed.

Manifests can be registered three ways, all equivalent:

- `PUT /api/datasets/{name}` with the manifest as the request body;
- a `.json` file in the manifest directory (`Loadstone:ManifestDirectory`), loaded at startup;
- in code, via `AddDataset(manifest)` or a compile-time `IDataset` implementation.

Property names are camelCase and case-insensitive. Enum values are camelCase strings.

## Top level

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Unique dataset name; also the default queue name. |
| `version` | string | `"1"` | Free-form version label. |
| `description` | string | – | Shown in the API. |
| `queue` | object | `{}` | Queue behavior, see below. |
| `source` | object | `{}` | Format options, see below. |
| `root` | entity | required | The root entity of the hierarchy. |

### `queue`

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | dataset name | Set the same name on several datasets to share one queue. |
| `maxAttempts` | int | `3` | Attempts before a job dead-letters. |
| `concurrency` | int | `1` | Parallel jobs processed from this queue. |

### `source`

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `formats` | string[] | all | Restrict accepted formats, e.g. `["xml", "csv"]`. |
| `json.rootProperty` | string | – | Property holding the record array when the document is an object. |
| `csv.delimiter` | string (1 char) | `,` | Field delimiter. |
| `csv.hasHeaderRow` | bool | `true` | First row is a header. Without it, columns map positionally to fields. |
| `csv.keyColumn` | string | `_key` | Row key column in hierarchical (zip) uploads. |
| `csv.parentKeyColumn` | string | `_parentKey` | Parent reference column in child files. |

## Entities

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Matches the XML element, JSON property, or CSV file name. |
| `table` | string | required | Target table. |
| `schema` | string | `dbo` | Target schema. |
| `keyColumn` | string | `Id` | Surrogate identity primary key in the target table. |
| `parentKeyColumn` | string | required on children | Foreign key column referencing the parent's `keyColumn`. |
| `naturalKey` | string[] | `[]` | Columns identifying a row for upsert, scoped within the parent. Empty = always insert. |
| `required` | bool | `false` | Parent records missing this child are rejected. |
| `fields` | field[] | required | At least one. |
| `children` | entity[] | `[]` | Nested entities, any depth. |

How hierarchy maps to tables: each entity gets its own table; children carry a foreign
key to their parent. During import, parents are merged first and the database keys they
get (or already had) are handed to their children — regardless of depth or fan-out.

## Fields

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Source name (element, attribute, property, or CSV header). |
| `column` | string | `name` | Target column, when it differs from the source name. |
| `type` | enum | `string` | `string`, `int32`, `int64`, `decimal`, `double`, `boolean`, `date`, `dateTime`, `time`, `guid`. |
| `source` | enum | `element` | `element`, `attribute` (XML attributes), or `constant` (always uses `default`). |
| `required` | bool | `false` | Missing/blank value rejects the record. |
| `maxLength` | int | – | Maximum string length; longer values reject the record. |
| `precision`, `scale` | int | `18`, `6` | Column precision for `decimal`. |
| `format` | string | – | Exact date/time format (e.g. `yyyyMMdd`); otherwise invariant parsing. |
| `default` | string | – | Used when the source value is blank. |
| `lookup` | object | – | Reference-data resolution, see below. |

Conversion notes: values are trimmed and parsed with the invariant culture. Booleans
accept `true/false`, `1/0`, `y/n`, `yes/no` (case-insensitive). Blank values become NULL
(or the `default`).

## Lookups

A field with a `lookup` stores the *resolved* value (for the built-in provider: the
code's integer id), not the raw source string.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `list` | string | required | Lookup list name (for `codelist`: the code list). |
| `provider` | string | `codelist` | Which registered `ILookupProvider` answers this lookup. |
| `onMissing` | enum | `rejectRecord` | `rejectRecord`, `rejectFile`, `useDefault`, `autoCreate`. |
| `caseInsensitive` | bool | `true` | Match codes case-insensitively. |
| `default` | string | – | Raw value resolved instead, with `useDefault`. |

Policies:

- **rejectRecord** — the record (and its subtree) goes to the rejection store; the rest
  of the file imports. The safe default.
- **rejectFile** — the whole import fails. For datasets where partial loads are worse
  than no load.
- **useDefault** — resolve `default` instead (e.g. an `UNKNOWN` code).
- **autoCreate** — add the value to the list and continue. Convenient for lists that are
  descriptive rather than authoritative.

Manage the built-in code lists over HTTP: `GET /api/codelists`,
`GET /api/codelists/{list}`, and `PUT /api/codelists/{list}` with
`[{ "code": "DK", "description": "Denmark" }, ...]`.

## Formats and shapes

**XML** — the reader streams the document and materializes one root-entity element at a
time, wherever it appears. Child elements matching entity names recurse; elements or
attributes matching field names become values; anything else is skipped. Line numbers are
kept for rejection reports.

**JSON** — a top-level array is streamed element by element. A top-level object works too
when it wraps the array in a property (`source.json.rootProperty`, or a property named
after the root entity). Properties matching child entities may hold an array or a single
object.

**CSV** — a dataset without children imports from a single CSV file. Hierarchical
datasets import from a **zip archive** with one `EntityName.csv` per entity, where each
file has a `_key` column and child files reference their parent through `_parentKey`.
Keys are only used to link rows inside the archive; they never reach the database.

## Upserts

With a `naturalKey`, re-importing a file is idempotent: existing rows (matched within
their parent) are updated, new rows are inserted, and counts of each are reported on the
job. Without a natural key every import appends. A unique index over
(`parentKeyColumn` + `naturalKey`) is included in the generated DDL and recommended on
pre-existing tables.
