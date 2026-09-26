// Syncs discovered events into SQL Server table S4K_AXS_SourceEvents
// (IntegrationTemplateSourceEvent model, SettingFactory.GetIntegrationSourceEventsTableName;
// the table is created by AXSDBCreator). Copied from BroadwayDirectShowDiscovery.App -
// only the table name and the row mapping (PerformerID, Description) differ.
"use strict";

const sql = require("mssql");

const TABLE = "S4K_AXS_SourceEvents";
const DESCRIPTION_MAX = 1000;

/** AXS descriptions are full HTML pages (often 5KB+). Description is a display
 * column (IntegrationTemplateSourceEvent, DisplayOrder 700), so store readable
 * text: tags and common entities removed, whitespace collapsed, capped. */
function descriptionText(html) {
  if (!html) return null;
  const text = html
    .replace(/<(script|style)[\s\S]*?<\/\1>/gi, " ")
    .replace(/<\/?(p|br|div|h[1-6]|li|ul|ol|tr|td|table)\b[^>]*>/gi, " ") // block tags separate words
    .replace(/<[^>]+>/g, "") // inline tags (<strong>, <a>...) don't
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&quot;/g, '"')
    .replace(/&#39;|&rsquo;|&lsquo;/g, "'")
    .replace(/&ldquo;|&rdquo;/g, '"')
    .replace(/&mdash;|&ndash;/g, "-")
    .replace(/&reg;/g, "(R)")
    .replace(/&[a-z]+;/gi, " ")
    .replace(/\s+/g, " ")
    .trim();
  if (!text) return null;
  return text.length > DESCRIPTION_MAX ? text.slice(0, DESCRIPTION_MAX - 3) + "..." : text;
}

/** The crawler's `eventDatetimeUtc` already carries "Z" for AXS (events.ts
 * adds it), but keep Broadway's guard: a UTC string with no trailing
 * "Z"/offset - `new Date(...)` on a string like that parses it as LOCAL
 * time (per the ECMA-262 date-time string spec), not UTC. Verified against
 * a real SQL Server: without this, EventDateUTC landed 7 hours off on a
 * UTC+7 machine. Force UTC interpretation by appending "Z" when there's no
 * timezone designator already. */
function toUtcDate(isoLike) {
  const hasTz = /Z$|[+-]\d{2}:?\d{2}$/.test(isoLike);
  return new Date(hasTz ? isoLike : `${isoLike}Z`);
}

/** Parses the same ADO.NET-style connection string already used elsewhere
 * in this repo's App.config files (Data Source=...;Initial Catalog=...;...)
 * into an `mssql` config object. Supports SQL Authentication (User ID/
 * Password) and named instances (Server\Instance) - see the "Known
 * limitations" note in README.md for what's NOT supported. */
function parseAdoConnectionString(connStr) {
  const parts = {};
  for (const piece of connStr.split(";")) {
    const idx = piece.indexOf("=");
    if (idx === -1) continue;
    const key = piece.slice(0, idx).trim().toLowerCase();
    const value = piece.slice(idx + 1).trim();
    if (key) parts[key] = value;
  }

  const dataSource = parts["data source"] || parts["server"] || parts["addr"] || parts["address"];
  if (!dataSource) throw new Error('connection string is missing "Data Source"/"Server"');
  let server = dataSource;
  let instanceName;
  let port;
  if (server.includes(",")) {
    const [host, p] = server.split(",");
    server = host;
    port = Number(p);
  }
  if (server.includes("\\")) {
    const [host, inst] = server.split("\\");
    server = host;
    instanceName = inst;
  }
  if (/^\(localdb\)/i.test(server)) {
    throw new Error(
      "(localdb) instances are not reachable from this app - SQL Server LocalDB uses a " +
        "dynamically-assigned named pipe that only ADO.NET clients can discover. Point at a " +
        "real SQL Server/Express instance's TCP endpoint instead.",
    );
  }
  server = server.replace(/^\(|\)$/g, ""); // strip stray parens if someone pastes "(host)"

  const database = parts["initial catalog"] || parts["database"];
  if (!database) throw new Error('connection string is missing "Initial Catalog"/"Database"');

  const user = parts["user id"] || parts["uid"] || parts["user"];
  const password = parts["password"] || parts["pwd"];
  const integratedSecurity = /^(true|sspi|yes)$/i.test(parts["integrated security"] || "");

  if (!user && integratedSecurity) {
    throw new Error(
      'this connection string uses "Integrated Security" (Windows Authentication) with no ' +
        "User ID/Password fallback - this app can't silently reuse the current Windows login " +
        "the way ADO.NET does. Add \"User ID=...;Password=...\" to the connection string (SQL " +
        "Authentication) instead.",
    );
  }
  if (!user || !password) {
    throw new Error('connection string is missing "User ID"/"Password" (SQL Authentication)');
  }

  const encrypt = /^(true|yes)$/i.test(parts["encrypt"] || "false");
  const trustServerCertificate = /^(true|yes)$/i.test(parts["trustservercertificate"] || "false");
  const connectTimeoutSec = Number(parts["connect timeout"]) || 30;

  const config = {
    server,
    database,
    user,
    password,
    port,
    connectionTimeout: connectTimeoutSec * 1000,
    options: { encrypt, trustServerCertificate },
  };
  if (instanceName) config.options.instanceName = instanceName;
  return config;
}

/** Upserts `rows` (EventRow[] from the crawler) into S4K_AXS_SourceEvents,
 * keyed by EventID. Uses a session temp table + one MERGE instead of one
 * round trip per row - rows can number in the thousands. */
async function syncSourceEvents(connectionString, rows, onLog) {
  const log = onLog || (() => {});
  if (rows.length === 0) {
    log("  (0 rows to sync - skipping SQL sync)");
    return { ok: true, count: 0 };
  }

  const config = parseAdoConnectionString(connectionString);
  const pool = new sql.ConnectionPool(config);
  try {
    await pool.connect();

    // IMPORTANT, both verified against a real SQL Server container while
    // building this (not assumed):
    //
    // 1. A local temp table (#x) only lives on the connection that created
    //    it, and ConnectionPool.request() can hand out a *different* pooled
    //    connection on every call - the CREATE TABLE, bulk insert and MERGE
    //    below would each silently land on a different connection and fail
    //    with "Invalid object name '#StageSourceEvents'". A Transaction
    //    pins every request created from it to the one connection it began
    //    on, so wrap the whole sequence in one even though we don't
    //    strictly need rollback semantics otherwise.
    //
    // 2. mssql's `.query()` runs through sp_executesql (an RPC call), which
    //    gives the local temp table its OWN nested scope - it's gone as
    //    soon as that one .query() call returns, even reusing the exact
    //    same connection/transaction from point 1. `.batch()` runs a plain
    //    T-SQL batch instead (no extra scope), which is what preserves the
    //    temp table for the bulk load and MERGE that follow. So CREATE
    //    TABLE and the MERGE both use `.batch()`, not `.query()`.

    // Some catalogs predate the .NET side's EnsureEventDateEst migration
    // (IntegrationTemplateDBCreator.cs) and don't have this column yet -
    // mirror that code's own ColumnExists guard so this sync degrades the
    // same way instead of hard-failing on an un-migrated table.
    const hasEstColumn = (
      await pool.request().query(
        `SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('${TABLE}') AND name = 'EventDateEST'`,
      )
    ).recordset.length > 0;
    if (!hasEstColumn) {
      log(`  (note: ${TABLE} has no EventDateEST column yet - skipping it, run the app's DB migration to add it)`);
    }

    const transaction = new sql.Transaction(pool);
    await transaction.begin();
    try {
      await new sql.Request(transaction).batch(`
        CREATE TABLE #StageSourceEvents (
            EventID nvarchar(100) NOT NULL,
            EventName nvarchar(250) NOT NULL,
            EventDateUTC datetime NOT NULL,
            -- Same Eastern wall-clock conversion IntegrationTemplateDBCreator.cs uses for
            -- this column on the real table (EnsureEventDateEst) - computed in SQL, not
            -- Node, so it can never drift from the .NET side's DST handling. Not PERSISTED:
            -- SQL Server rejects persisting an AT TIME ZONE expression as "non-deterministic"
            -- (verified against a real SQL Server 2019 instance) - fine here since this is a
            -- one-shot staging table, never indexed or reread after the MERGE below.
            EventDateEST AS (CONVERT(datetime, EventDateUTC AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time')),
            EventUrl nvarchar(1000) NULL,
            PerformerID nvarchar(250) NULL,
            Venue nvarchar(250) NULL,
            City nvarchar(250) NULL,
            Description nvarchar(max) NULL,
            Enabled bit NOT NULL
                CONSTRAINT DF_StageSourceEvents_Enabled DEFAULT (1),

            CONSTRAINT PK_StageSourceEvents
                PRIMARY KEY CLUSTERED (EventID ASC)
                WITH (
                    PAD_INDEX = OFF,
                    STATISTICS_NORECOMPUTE = OFF,
                    IGNORE_DUP_KEY = OFF,
                    ALLOW_ROW_LOCKS = ON,
                    ALLOW_PAGE_LOCKS = ON,
                    OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF
                )
        )
    `);

      const table = new sql.Table("#StageSourceEvents");
      table.create = false;
      table.columns.add("EventID", sql.NVarChar(100), { nullable: false });
      table.columns.add("EventName", sql.NVarChar(250), { nullable: false });
      table.columns.add("EventDateUTC", sql.DateTime, { nullable: false });
      table.columns.add("EventUrl", sql.NVarChar(1000), { nullable: true });
      table.columns.add("PerformerID", sql.NVarChar(250), { nullable: true });
      table.columns.add("Venue", sql.NVarChar(250), { nullable: true });
      table.columns.add("City", sql.NVarChar(250), { nullable: true });
      table.columns.add("Description", sql.NVarChar(sql.MAX), { nullable: true });
      table.columns.add("Enabled", sql.Bit, { nullable: false });

      for (const r of rows) {
        table.rows.add(
          String(r.eventId),
          (r.eventName || "").slice(0, 250),
          toUtcDate(r.eventDatetimeUtc || r.eventDatetime),
          r.eventUrl || null,
          r.performerIds ? r.performerIds.slice(0, 250) : null, // AXS performer ids, comma-joined
          r.venue ? r.venue.slice(0, 250) : null,
          r.city ? r.city.slice(0, 250) : null,
          descriptionText(r.description),
          true,
        );
      }
      await new sql.Request(transaction).bulk(table);

      const estSet = hasEstColumn ? "\n          EventDateEST = source.EventDateEST," : "";
      const estInsertCol = hasEstColumn ? ", EventDateEST" : "";
      const estInsertVal = hasEstColumn ? ", source.EventDateEST" : "";
      const mergeResult = await new sql.Request(transaction).batch(`
        MERGE INTO ${TABLE} AS target
        USING #StageSourceEvents AS source
          ON target.EventID = source.EventID
        WHEN MATCHED THEN UPDATE SET
          EventName = source.EventName,
          EventDateUTC = source.EventDateUTC,${estSet}
          EventUrl = source.EventUrl,
          PerformerID = source.PerformerID,
          Venue = source.Venue,
          City = source.City,
          Description = source.Description,
          Enabled = source.Enabled
        WHEN NOT MATCHED BY TARGET THEN INSERT
          (EventID, EventName, EventDateUTC${estInsertCol}, EventUrl, PerformerID, Venue, City, Description, Enabled)
          VALUES
          (source.EventID, source.EventName, source.EventDateUTC${estInsertVal}, source.EventUrl,
           source.PerformerID, source.Venue, source.City, source.Description, source.Enabled)
        OUTPUT $action;
      `);
      await transaction.commit();

      const actions = mergeResult.recordset.map((r) => r.$action);
      const inserted = actions.filter((a) => a === "INSERT").length;
      const updated = actions.filter((a) => a === "UPDATE").length;
      log(`  Synced to SQL: ${inserted} inserted, ${updated} updated in ${TABLE}`);
      return { ok: true, count: rows.length, inserted, updated };
    } catch (err) {
      await transaction.rollback().catch(() => {});
      throw err;
    }
  } finally {
    await pool.close();
  }
}

module.exports = { syncSourceEvents };
