---
title: Recipes
---

# Recipes

Practical patterns for the three most common inbound-data situations. Each recipe is a
complete approach: the manifest ideas, the upload, and what to watch afterwards.

## 1. A partner's nested XML feed

**Situation:** a partner sends you order/shipment/measurement files — a root element
repeated many times, each with nested, sometimes optional children, and reference codes
that must match your master data.

**Approach:**

- Model each nesting level as an entity; mark children `"required": true` only where a
  missing child genuinely invalidates the parent.
- Give every entity a `naturalKey` that is unique *within its parent* (sequence numbers,
  line numbers, external ids). Re-imports then become idempotent updates, so the partner
  can resend corrected files freely.
- Put lookups on every code field. For codes that must match master data, use
  `"onMissing": "rejectRecord"`; the record lands in the rejection report with its source
  line while the rest of the file imports. Reserve `"rejectFile"` for fields where a bad
  value discredits the whole file (a country code, a schema version).
- Fields can read XML attributes with `"source": "attribute"`.

Extra elements the partner adds later are ignored, so their schema evolution doesn't
break your import. Watch `GET /api/imports/{id}/rejections` after each delivery — it is
effectively a data-quality report you can send back to the partner.

## 2. A SaaS tool's JSON export

**Situation:** a nightly export from a CRM/e-commerce/ticketing API: a large JSON array
of objects with embedded arrays (customers with addresses, tickets with comments).

**Approach:**

- A top-level JSON array streams with bounded memory regardless of file size. If the
  export wraps the array (`{ "items": [...] }`), set `"source": { "json": { "rootProperty": "items" } }`.
- JSON property matching is case-insensitive, so `orderNumber` in the file maps to the
  `OrderNumber` field without configuration.
- Numbers, booleans, and nulls convert from their JSON representation; declare the field
  `type` you want in the database and let conversion enforce it.
- Embedded single objects (not arrays) also work as children — one child record.

Schedule the upload with any HTTP client (the export job itself, cron + curl, Power
Automate). The response's `jobId` is your handle for monitoring.

## 3. Spreadsheet-driven CSV, curated by a team

**Situation:** a team maintains data in spreadsheets — one sheet per level (orders /
order lines, sites / measurements) — and needs to load it reliably without hand-holding.

**Approach:**

- Flat, single-table datasets import from one CSV file directly.
- For hierarchies, each sheet exports to `EntityName.csv` with a `_key` column, and child
  sheets add `_parentKey`; zip the files and upload the archive. The keys only link rows
  inside the archive — they never reach the database.
- Quoted fields, embedded commas, and line breaks inside quotes are handled (RFC 4180);
  delimiters are configurable (`"csv": { "delimiter": ";" }` for locales where Excel
  exports semicolons).
- Typos in code columns are the most common failure: choose `"onMissing": "rejectRecord"`
  and give the team the rejection report URL — it names the file, line, field, and raw
  value, which is exactly what they need to fix the sheet.

**Tip for all three:** register the dataset with version control by keeping the manifest
JSON in your repository and deploying it to the manifest directory, or `PUT` it during
CI. The manifest *is* the interface contract with your data suppliers.
