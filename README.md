# WellWorks Transaction Ingestion — Local Setup Guide

Follow these steps in order. Total time: ~10 minutes.

---

## Step 1 — Set Up the Database in SSMS

1. Open **SQL Server Management Studio (SSMS)**
2. Connect to your local instance (usually `localhost` or `.\SQLEXPRESS`)
3. Open the file: `scripts/01_Setup_Database.sql`
4. Press **F5** (or click Execute)

You should see output like:
```
Database WellWorksIngestion created.
Table dbo.Transactions created.
Table dbo.TransactionIngestionLog created.
Type dbo.TransactionStagingType created.
All objects verified. You are ready to run the API.
```

> If you see "already exists" messages on a re-run, that's fine — the script is idempotent.

---

## Step 2 — Check Your Connection String

Open `WellWorksIngestion.Api/appsettings.json`.

The default connection string is:
```
Server=localhost;Database=WellWorksIngestion;Trusted_Connection=True;TrustServerCertificate=True;
```

**If your SQL Server instance has a different name** (e.g. `.\SQLEXPRESS` or `localhost\SQLEXPRESS`),
update the `Server=` value to match. You can find your instance name in SSMS in the
"Connect to Server" dialog — it's whatever appears in the "Server name" field.

---

## Step 3 — Open in Visual Studio and Run

1. Open `WellWorksIngestion.sln` in **Visual Studio 2022**
2. Set `WellWorksIngestion.Api` as the startup project (right-click → Set as Startup Project)
3. Press **F5**

Visual Studio will restore NuGet packages automatically on first build.

Your browser will open to `https://localhost:7200` showing the **Swagger UI**.

---

## Step 4 — Send a Test Batch via Swagger

1. In Swagger, click **POST /api/transactions/ingest** → **Try it out**
2. Paste this JSON into the request body:

```json
[
  { "transactionID": "TXN-001", "memberID": "MBR-100", "transactionDate": "2026-01-15T10:00:00", "transactionAmount": 149.99 },
  { "transactionID": "TXN-002", "memberID": "MBR-101", "transactionDate": "2026-01-15T10:05:00", "transactionAmount": 75.00 },
  { "transactionID": "TXN-003", "memberID": "MBR-100", "transactionDate": "2026-01-15T10:10:00", "transactionAmount": 299.00 },
  { "transactionID": "TXN-001", "memberID": "MBR-100", "transactionDate": "2026-01-15T10:00:00", "transactionAmount": 149.99 },
  { "transactionID": "TXN-004", "memberID": "MBR-102", "transactionDate": "2026-01-15T10:15:00", "transactionAmount": 12.50 },
  { "transactionID": "TXN-BAD", "memberID": "MBR-X",  "transactionDate": "2026-01-15T10:20:00", "transactionAmount": -5.00 }
]
```

3. Click **Execute**

Expected response:
```json
{
  "batchId": "...",
  "totalReceived": 6,
  "inserted": 4,
  "skipped": 0,
  "validationFailures": 1,
  "intraBatchDuplicates": 1,
  "message": "Batch ...: 4 inserted, 0 skipped (inter-batch duplicates), 1 intra-batch duplicates, 1 validation failures."
}
```

**Send the same batch a second time** — you'll see `skipped: 4` because those TransactionIDs now exist.

---

## Step 5 — Inspect Results in SSMS

Open `scripts/02_TestData_And_Queries.sql` in SSMS and run the queries in Section C
to see what was inserted and what was logged.

---

## Step 6 — Run Unit Tests

In Visual Studio: **Test → Run All Tests** (Ctrl+R, A)

All 5 tests should go green. These tests run with no database — the repository is mocked.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Login failed for user` | Change `Trusted_Connection=True` to `User Id=sa;Password=yourpass;` in appsettings.json |
| `Cannot open database WellWorksIngestion` | Re-run `01_Setup_Database.sql` in SSMS |
| `A network-related error...` | Check the `Server=` value in appsettings.json matches your SSMS instance name |
| Port 7200 already in use | Change `applicationUrl` in `Properties/launchSettings.json` |
| SSL certificate warning in browser | Click Advanced → Proceed, or run `dotnet dev-certs https --trust` in a terminal |
