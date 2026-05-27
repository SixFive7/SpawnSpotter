using System.Globalization;
using System.Text;
using System.Text.Json;
using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>
/// Writes a single-file HTML report on graceful shutdown (plan 5.7 / step 12).
/// Reads back the day's JSONL (or uses the in-memory accumulator), produces a
/// self-contained HTML page with embedded CSS+JS, sortable table, classification
/// filter, expandable rows showing the full parent chain.
/// </summary>
internal static class HtmlReportWriter
{
    /// <summary>
    /// Writes the HTML report. <paramref name="inMemory"/> is preferred for richness;
    /// if null, we try to read back today's JSONL file at <paramref name="jsonlPath"/>.
    /// </summary>
    public static async Task WriteAsync(string outputPath, IReadOnlyList<EventRecord>? inMemory, string? jsonlPath)
    {
        var events = new List<JsonEvent>();
        if (inMemory is { Count: > 0 })
        {
            foreach (var r in inMemory)
            {
                events.Add(JsonlExporter.Build(r));
            }
        }
        else if (jsonlPath is not null && File.Exists(jsonlPath))
        {
            await foreach (var line in File.ReadLinesAsync(jsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                try
                {
                    var parsed = JsonSerializer.Deserialize(line, JsonExportContext.Default.JsonEvent);
                    if (parsed is not null) { events.Add(parsed); }
                }
                catch
                {
                    // Skip malformed lines (e.g. partial write at the tail).
                }
            }
        }

        var dataJson = JsonSerializer.Serialize(events, JsonExportContext.Default.ListJsonEvent);
        var html = RenderHtml(dataJson, events.Count);
        await File.WriteAllTextAsync(outputPath, html, new UTF8Encoding(false));
    }

    private static string RenderHtml(string dataJson, int eventCount)
    {
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(8 * 1024 + dataJson.Length);
        sb.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>SpawnSpotter Report</title>
<style>
  body { font-family: -apple-system, Segoe UI, system-ui, sans-serif; margin: 0; padding: 16px; background: #1e1e1e; color: #e1e1e1; }
  h1 { margin-top: 0; }
  .meta { color: #888; margin-bottom: 12px; font-size: 13px; }
  .filters { margin: 8px 0 16px; display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  select, input { background: #2d2d2d; color: #e1e1e1; border: 1px solid #444; padding: 4px 8px; border-radius: 4px; font: inherit; }
  table { border-collapse: collapse; width: 100%; font-size: 13px; }
  th, td { padding: 6px 8px; border-bottom: 1px solid #333; text-align: left; vertical-align: top; }
  th { background: #2a2a2a; cursor: pointer; user-select: none; position: sticky; top: 0; }
  th:hover { background: #353535; }
  tr:hover { background: #252525; }
  tr.detail-row { background: #181818; }
  tr.detail-row td { padding: 8px 16px; }
  pre { font: 12px/1.4 Consolas, monospace; background: #111; padding: 8px; overflow-x: auto; border-radius: 4px; }
  .cls-STEAL { color: #ff7373; font-weight: 600; }
  .cls-MAYBE_STEAL { color: #ffb347; font-weight: 600; }
  .cls-SESSION_LOCK { color: #c39bff; }
  .cls-USER_ALT_TAB, .cls-USER_CLICK, .cls-USER_OTHER { color: #79bdff; }
  .cls-SHELL_TRANSIENT { color: #888; font-style: italic; }
  .cls-PIPELINE_PRESSURE { color: #ffd87b; }
  .toggle { cursor: pointer; color: #888; user-select: none; }
  .toggle:hover { color: #ddd; }
  .empty { color: #888; }
</style>
</head>
<body>
<h1>SpawnSpotter Report</h1>
""");
        sb.Append("<div class=\"meta\">Generated ").Append(stamp).Append(" - ").Append(eventCount).Append(" event").Append(eventCount == 1 ? "" : "s").Append(".</div>");
        sb.Append("""
<div class="filters">
  <label>Filter:
    <select id="cls">
      <option value="">All classifications</option>
      <option>STEAL</option>
      <option>MAYBE_STEAL</option>
      <option>SESSION_LOCK</option>
      <option>USER_ALT_TAB</option>
      <option>USER_CLICK</option>
      <option>USER_OTHER</option>
      <option>SHELL_TRANSIENT</option>
      <option>PIPELINE_PRESSURE</option>
    </select>
  </label>
  <label>Search title/class:
    <input id="q" type="search" placeholder="substring">
  </label>
</div>
<table id="tbl">
  <thead>
    <tr>
      <th data-k="timestamp_utc">Timestamp UTC</th>
      <th data-k="classification">Classification</th>
      <th data-k="monitored_via">Via</th>
      <th data-k="focused_pid">PID</th>
      <th data-k="window_class">Window class</th>
      <th data-k="window_title">Title</th>
      <th>Chain</th>
      <th>Note</th>
    </tr>
  </thead>
  <tbody></tbody>
</table>
<script>
const DATA =
""");
        sb.Append(dataJson);
        sb.Append(";\n");
        sb.Append("""
function escape(s) { return (s ?? '').toString().replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
function chainSummary(c) { return (c ?? []).map(n => n.pid + ':' + (n.basename || '?')).join(' ◄ '); }
function chainDetail(c) {
  if (!c || !c.length) { return '<span class="empty">no chain</span>'; }
  const rows = c.map(n => {
    let html = '<div><strong>' + escape(n.pid) + '</strong> ' + escape(n.basename) + '</div>';
    html += '<div><code>' + escape(n.image_path) + '</code></div>';
    if (n.command_line) html += '<div>cmd: <code>' + escape(n.command_line) + '</code></div>';
    if (n.cwd) html += '<div>cwd: <code>' + escape(n.cwd) + '</code></div>';
    if (n.package_aumi) html += '<div>aumi: <code>' + escape(n.package_aumi) + '</code></div>';
    if (n.note) html += '<div><em>' + escape(n.note) + '</em></div>';
    return html;
  });
  return '<pre>' + rows.join('\n----\n') + '</pre>';
}
function rowsFor(events) {
  const tbody = document.querySelector('#tbl tbody');
  tbody.innerHTML = '';
  if (!events.length) {
    tbody.innerHTML = '<tr><td colspan="8" class="empty">No matching events.</td></tr>';
    return;
  }
  for (const e of events) {
    const row = document.createElement('tr');
    row.innerHTML =
      '<td>' + escape(e.timestamp_utc) + '</td>' +
      '<td class="cls-' + escape(e.classification) + '">' + escape(e.classification) + '</td>' +
      '<td>' + escape(e.monitored_via) + '</td>' +
      '<td>' + escape(e.focused_pid) + '</td>' +
      '<td>' + escape(e.window_class) + '</td>' +
      '<td>' + escape(e.window_title) + '</td>' +
      '<td><span class="toggle">' + escape(chainSummary(e.parent_chain)) + '</span></td>' +
      '<td>' + escape(e.note) + '</td>';
    tbody.appendChild(row);
    const toggle = row.querySelector('.toggle');
    toggle.addEventListener('click', () => {
      const next = row.nextElementSibling;
      if (next && next.classList.contains('detail-row')) { next.remove(); return; }
      const dr = document.createElement('tr');
      dr.className = 'detail-row';
      dr.innerHTML = '<td></td><td colspan="7">' + chainDetail(e.parent_chain) + '</td>';
      row.insertAdjacentElement('afterend', dr);
    });
  }
}
let sortKey = 'timestamp_utc';
let sortDesc = true;
function refresh() {
  const cls = document.getElementById('cls').value;
  const q = document.getElementById('q').value.toLowerCase();
  const filtered = DATA.filter(e =>
    (!cls || e.classification === cls) &&
    (!q || (e.window_title ?? '').toLowerCase().includes(q) || (e.window_class ?? '').toLowerCase().includes(q))
  );
  filtered.sort((a, b) => {
    const va = a[sortKey], vb = b[sortKey];
    if (va < vb) return sortDesc ? 1 : -1;
    if (va > vb) return sortDesc ? -1 : 1;
    return 0;
  });
  rowsFor(filtered);
}
document.querySelectorAll('#tbl th[data-k]').forEach(th => th.addEventListener('click', () => {
  const k = th.getAttribute('data-k');
  if (sortKey === k) { sortDesc = !sortDesc; } else { sortKey = k; sortDesc = false; }
  refresh();
}));
document.getElementById('cls').addEventListener('change', refresh);
document.getElementById('q').addEventListener('input', refresh);
refresh();
</script>
</body>
</html>
""");
        return sb.ToString();
    }
}
