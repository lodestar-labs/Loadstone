# README demo GIF — storyboard

Goal: a ~30-second animated GIF at the top of the README showing file-to-rows in one
take. Target: 1000×625 px (GitHub README width), under 10 MB, 12–15 fps.

Recording tools: ScreenToGif (Windows) or LICEcap. Record at 100% browser zoom, light
theme, a clean profile window with no bookmarks bar.

Setup before recording (not shown in the GIF):

1. `docker compose up --build` and wait for the dashboard to be healthy.
2. Have `samples/orders/orders.xml` on the desktop or in the file picker's recent list.
3. Prepare a *second* file, `orders-with-errors.xml`, by copying the sample and breaking
   one record (e.g. `Total` = `banana`) so the rejection view has content.
4. Empty state: restart the containers (`docker compose down -v && docker compose up`)
   so the jobs table starts clean.

| # | Duration | Shot | Notes |
| --- | --- | --- | --- |
| 1 | 3 s | Dashboard at `http://localhost:8080`, Imports view, empty jobs table, health badge green | Establishes "this is a real running product". Pause on it briefly. |
| 2 | 4 s | Click the dataset dropdown (shows `orders`), pick `orders-with-errors.xml`, click **Upload & queue** | Mouse moves deliberately; no scrubbing. Toast appears: "Queued as job …". |
| 3 | 4 s | The job row appears: Pending → Processing → CompletedWithRejections, counters filling in | The 3-second auto-refresh does this on its own; don't touch anything. |
| 4 | 5 s | Click the job row → detail view. Hold on the timeline (start → complete with counts) | Shows observability without narration. |
| 5 | 6 s | Scroll/hold on **Rejected records**: entity, line number, field `Total`, reason, raw value `banana` | This is the money shot — the differentiator. Hold it longest. |
| 6 | 4 s | Switch to a database client (SSMS/Azure Data Studio, pre-opened, query ready): `SELECT TOP 5 * FROM Orders JOIN OrderLines ...` → run → rows | Proves real relational rows with wired foreign keys. Font size ≥ 14 pt. |
| 7 | 3 s | Back to the dashboard, brief hold on the full jobs table | Clean ending frame; this frame doubles as the static preview. |

Editing:

- Trim dead frames between shots; keep total ≤ 30 s.
- No cursor highlighting effects, no captions — the UI text carries it.
- Export once as GIF for the README and once as MP4 (for the docs site / social posts).
- Place in the README directly under the tagline: `![Loadstone demo](docs/assets/demo.gif)`
  with the file committed under `docs/assets/`.
