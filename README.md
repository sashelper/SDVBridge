SDVBridge – Enterprise Guide REST Bridge
========================================

SDVBridge is a SAS Enterprise Guide (EG) custom task that exposes the SAS metadata tree and dataset download workflow through a lightweight HTTP API. Once loaded inside EG, the task spins up a local `HttpListener` that listens on `http://127.0.0.1:<port>/` and serves JSON so any desktop tool can enumerate servers, libraries, members, and export datasets out of EG.

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

The server only binds to loopback, enables permissive CORS headers for local tools, and logs to `%TEMP%\SDVBridge\Logs\SDVBridge.log`. Dataset exports are written to `%TEMP%\SDVBridge\<server>\<libref>\<member>.sas7bdat`; delete those files when you are done.

REST API overview
-----------------
All payloads are UTF-8 JSON with lowercase property names. See `API_GUIDE.md` for complete examples.

Endpoint | Description
-------- | -----------
`GET /servers` | Lists SAS servers visible to the EG session.
`GET /servers/{server}/libraries` | Lists libraries (assigning them if necessary).
`GET /servers/{server}/libraries/{libref}/datasets` | Lists datasets within a library.
`POST /datasets/open` | Body: `{"server":"SASApp","libref":"SASHELP","member":"CLASS"}`. Downloads the dataset to `%TEMP%\SDVBridge`, keeps the native member name, and returns `{ "path": "C:\\...\\CLASS.sas7bdat", "filename": "CLASS.sas7bdat" }`.

Mock server workflow
--------------------
Use the mock when you only need the HTTP surface:

```
dotnet run --project MockServer/MockServer.csproj -- --port 17832
```

The mock returns deterministic metadata, writes plain-text `.sas7bdat` placeholders under `%TEMP%\SDVBridge`, and mirrors the same endpoints/JSON casing, so client applications can be exercised without bringing up EG or SAS.

Troubleshooting
---------------
- Check `%TEMP%\SDVBridge\Logs\SDVBridge.log` for server and export diagnostics.
- `netstat -ano | find "17832"` can help confirm the listener is running.
- If SAS assemblies cannot be resolved at build time, point `SasEgPath` to your EG install directory or copy the DLLs next to the project.
