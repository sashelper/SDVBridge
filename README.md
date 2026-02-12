SDVBridge – Enterprise Guide REST Bridge
========================================

SDVBridge is a SAS Enterprise Guide (EG) custom task that exposes the SAS metadata tree, SAS program submission workflow, and dataset download workflow through a lightweight HTTP API. Once loaded inside EG, the task spins up a local `HttpListener` that listens on `http://127.0.0.1:<port>/` and serves JSON so any desktop tool can enumerate servers/libraries/members, submit SAS code, inspect job status/log/output/artifacts, and export datasets out of EG.

Repository layout
-----------------
- `SDVBridge/` – WinForms-based EG custom task (`ServerControlTask`) and the in-process REST server (`Server/*`).
- `MockServer/` – stand-alone .NET 8 minimal API that mimics the same endpoints when you do not have SAS installed.
- `API_GUIDE.md` – more detailed request/response samples for each REST endpoint.
- `SDVBridge.sln` – Visual Studio solution for the add-in project.

Prerequisites
-------------
Add-in requirements:
- Windows with SAS Enterprise Guide 8.2 (or newer) that includes the SAS Custom Task SDK.
- Visual Studio 2019+ (or MSBuild) with .NET Framework 4.7.2 targeting pack.
- Access to the SAS assemblies `SAS.Shared.AddIns.dll`, `SAS.Tasks.Toolkit.dll`, `SASInterop.dll`, and `SASIOMCommonInterop.dll`.
  - By default the project looks under `$(ProgramFiles)\SASHome\SASEnterpriseGuide\8.2`.
  - If EG is installed elsewhere, pass `/p:SasEgPath="D:\Path\To\SASEnterpriseGuide\8.2"` when building or copy the DLLs beside `SDVBridge.csproj`.

Mock server requirements:
- .NET 8 SDK (for `dotnet run`).

Building the add-in
-------------------
1. Open `SDVBridge.sln` in Visual Studio, pick the desired configuration, and build the `SDVBridge` project.
2. Or build from a Developer Command Prompt:

   ```
   msbuild SDVBridge.sln /t:Rebuild /p:Configuration=Release /p:SasEgPath="D:\Apps\SAS\EG82"
   ```

The output assembly is written to `SDVBridge/bin/<Configuration>/SDVBridge.dll`.

Deploying into Enterprise Guide
-------------------------------
1. Close Enterprise Guide.
2. Copy `SDVBridge/bin/<Configuration>/SDVBridge.dll` into `%AppData%\SAS\EnterpriseGuide\Custom` (create the folder if it does not exist).
3. Start EG; the add-in appears under **Tasks ▸ Helper Software ▸ SDVBridge**.

Running the SDVBridge REST server inside EG
-------------------------------------------
1. Launch **Tasks ▸ Helper Software ▸ SDVBridge**.
2. Pick a TCP port (default `17832`) and click **Start**. The task displays the listener URL while it is active.
3. Keep the dialog open while clients connect; click **Stop** before closing EG.
4. If Windows blocks the listener, grant the HTTP reservation once:

   ```
   netsh http add urlacl url=http://127.0.0.1:17832/ user=%USERNAME%
   ```

Server capture paths (recommended for remote SAS)
-------------------------------------------------
The control form includes two optional fields:
- `Server Log Path`
- `Server Output Path`

When both are provided, SDVBridge writes SAS log/listing through `PROC PRINTTO` to those server-side files and downloads them back to local temp artifacts.

Important requirements:
- Provide both fields together (or leave both empty).
- The SAS workspace account must have write permission to the parent folder.
- Prefer absolute server paths that are valid on the SAS host.

The two values are persisted and auto-loaded next time from:
- `%APPDATA%\\SDVBridge\\settings.json`

The server only binds to loopback, enables permissive CORS headers for local tools, and logs to `%TEMP%\SDVBridge\Logs\SDVBridge.log`. Dataset exports are written to `%TEMP%\SDVBridge\<server>\<libref>\<member>.sas7bdat`; delete those files when you are done.

REST API overview
-----------------
All payloads are UTF-8 JSON with lowercase property names. For POST requests, set `Content-Type: application/json; charset=utf-8`. See `API_GUIDE.md` for complete examples.

Endpoint | Description
-------- | -----------
`GET /servers` | Lists SAS servers visible to the EG session.
`GET /servers/{server}/libraries` | Lists libraries (assigning them if necessary).
`GET /servers/{server}/libraries/{libref}/datasets` | Lists datasets within a library.
`GET /servers/{server}/libraries/{libref}/datasets/{member}/columns` | Lists columns for a dataset (name/label/type/length/format/informat).
`GET /servers/{server}/libraries/{libref}/datasets/{member}/preview?limit=20` | Returns tabular preview rows (up to `limit`, default 20, max 500).
`POST /datasets/open` | Body: `{"server":"SASApp","libref":"SASHELP","member":"CLASS"}`. Downloads the dataset to `%TEMP%\SDVBridge`, keeps the native member name, and returns `{ "path": "C:\\...\\CLASS.sas7bdat", "filename": "CLASS.sas7bdat" }`.
`POST /programs/submit` | Body: `{"server":"SASApp","code":"proc print data=sashelp.class(obs=5); run;"}`. Submits SAS code and returns a job id + status.
`POST /programs/submit/async` | Queues SAS code and returns immediately with `202 Accepted` + `jobid`.
`GET /jobs/{jobid}` | Returns job status (`queued/running/completed/failed`) and timestamps.
`GET /jobs/{jobid}/artifacts` | Returns generated result artifacts (e.g., HTML/PDF/Excel) with local file paths.
`GET /jobs/{jobid}/artifacts/{artifactid}` | Streams/downloads a specific artifact file by id (or artifact filename).
`GET /jobs/{jobid}/log` | Returns captured log text for a submitted job. Supports `?offset=<n>` for incremental polling.
`GET /jobs/{jobid}/output` | Returns captured listing/output text for a submitted job.

Mock server workflow
--------------------
Use the mock when you only need the HTTP surface:

```
dotnet run --project MockServer/MockServer.csproj -- --port 17832
```

The mock returns deterministic metadata, writes plain-text `.sas7bdat` placeholders under `%TEMP%\SDVBridge`, and mirrors the same endpoints/JSON casing (including program submit/log/output jobs), so client applications can be exercised without bringing up EG or SAS.

Troubleshooting
---------------
- Check `%TEMP%\SDVBridge\Logs\SDVBridge.log` for server and export diagnostics.
- `netstat -ano | find "17832"` can help confirm the listener is running.
- If SAS assemblies cannot be resolved at build time, point `SasEgPath` to your EG install directory or copy the DLLs next to the project.
