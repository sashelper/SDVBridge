SDVBridge – SAS Enterprise Guide Add-In
============================================

This repo holds a starter SAS Enterprise Guide 8.2 custom task written in C#.  
It now exposes two sample tasks:

- `HelloWorldTask` — shows a greeting to prove that the add-in loads.
- `ServerControlTask` — a minimal UI that starts or stops the built-in REST API for surfacing SAS metadata and binary datasets.

Prerequisites
-------------
- Windows with SAS Enterprise Guide 8.2 installed (ships the SAS custom task SDK).
- Visual Studio 2019+ or the .NET Framework 4.7.2 targeting pack/Build Tools.
- Access to the SAS assemblies `SAS.Shared.AddIns.dll`, `SAS.Tasks.Toolkit.dll`, `SASInterop.dll`, and `SASIOMCommonInterop.dll`.

The project expects the Enterprise Guide binaries (including the interop DLLs) under `$(ProgramFiles)\SASHome\SASEnterpriseGuide\8.2`.  
If EG is installed somewhere else, set the MSBuild property `SasEgPath` when you build:

```
msbuild SDVBridge.sln /p:SasEgPath="D:\Apps\SAS\EG82"
```

Building
--------
1. Open the solution in Visual Studio, restore the correct target framework, and build `SDVBridge`.
2. Or build from the Developer Command Prompt: `msbuild SDVBridge.sln /t:Rebuild /p:Configuration=Release`.

Deploying the add-in
--------------------
1. Locate the compiled DLL, e.g. `SDVBridge/bin/Release/SDVBridge.dll`.
2. Copy it into the Enterprise Guide custom tasks folder: `%AppData%\SAS\EnterpriseGuide\Custom`.
   (Create the folder if it does not exist. Enterprise Guide reads everything under this path on start-up.)
3. Launch Enterprise Guide 8.2 and look under **Tasks ▸ SDVBridge Samples**.

### Hello World task

- Choose **Hello World**. A message box should display “Hello from the SDVBridge Enterprise Guide add-in!”.

### REST Server Control task

1. Open **Tasks ▸ SDVBridge Samples ▸ REST Server Control**.
2. Enter the TCP port you want the REST API to listen on (default `17832`) and click **Start**.
3. The status label shows `http://127.0.0.1:<port>/` while the server is running; click **Stop** to shut it down when you are done.

### Embedded REST API

Once the server is running, external tools can call the following endpoints:

Endpoint | Description
-------- | -----------
`GET /servers` | Lists SAS servers visible to Enterprise Guide.
`GET /servers/{server}/libraries` | Lists libraries on the specified server (assigns them if needed).
`GET /servers/{server}/libraries/{libref}/datasets` | Lists datasets available inside the library.
`POST /datasets/open` | Body: `{"server":"SASApp","libref":"SASHELP","member":"CLASS"}`. Saves the dataset to `%TEMP%\SDVBridge\SASApp\SASHELP\CLASS.sas7bdat`, logs each step to the EG debug output, and returns `{ "path": "...\\CLASS.sas7bdat", "filename": "CLASS.sas7bdat" }`.

If Windows blocks the listener you may need to grant an HTTP reservation once, for example:

```
netsh http add urlacl url=http://127.0.0.1:17832/ user=%USERNAME%
```

Keep the REST Server Control window handy so you can start/stop the API as needed during your EG session.

All JSON payloads now use lowercase property names (for example `name`, `libref`, `isassigned`, `rowlimit`). The download endpoint creates a full `.sas7bdat` copy under `%TEMP%\SDVBridge\<server>\<libref>` on the EG workstation, keeps the dataset's original member name for the file (e.g., `CLASS.sas7bdat`), and returns the absolute path so other local tools can read it; clean up those files after use.

Mocking the API without SAS
---------------------------
If you only need the HTTP surface and do not have SAS Enterprise Guide available, run the lightweight mock included in `MockServer/` (a .NET 8 minimal API that mirrors the endpoints above with canned data).

1. `dotnet run --project MockServer/MockServer.csproj -- --port 17832`
2. Call the same endpoints (servers, libraries, datasets, `POST /datasets/open`) against the printed URL.

The mock responses contain deterministic sample metadata while dataset requests create a small text `.sas7bdat` placeholder under `%TEMP%\SDVBridge\<server>\<libref>` with the same filename convention and return its path so you can exercise client code paths without EG.

Next steps
----------
- Replace the message box with a WinForms UI (`SAS.Tasks.Toolkit.Controls.TaskForm`) and collect user input.
- Interact with the SAS session through the `Consumer` object (submit code, read data, create results).
- Add icons (via the `[IconLocation]` attribute) and localization for production-quality tasks.
- Add authentication/authorization to the REST API, persist port preferences, or expose additional endpoints (submit SAS code, push results into the EG project tree).
