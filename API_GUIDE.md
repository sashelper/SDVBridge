SDVBridge REST API Guide
=============================

Starting the Server
-------------------
1. Open SAS Enterprise Guide.
2. Launch **Tasks ▸ Helper Software ▸ SDVBridge**.
3. Choose a TCP port (default `17832`) and click **Start**. The status label shows the base URL (e.g., `http://127.0.0.1:17832/`).
4. Keep the control window open while third-party applications connect. Click **Stop** when finished.

The server listens only on loopback. If Windows blocks the listener, run (once):

```
netsh http add urlacl url=http://127.0.0.1:17832/ user=%USERNAME%
```

General Notes
-------------
- Base URL: `http://127.0.0.1:<port>/`
- All responses are UTF-8 JSON unless noted.
- Set `Content-Type: application/json` on POST requests.
- Property names in the JSON payloads are lowercase (e.g., `name`, `libref`, `rowlimit`).
- `/datasets/open` writes a `.sas7bdat` copy under `%TEMP%\SDVBridge\<server>\<libref>` and returns its full path so other local applications can open it.
- Program submit jobs are kept in-memory by the running task process (up to 200 recent jobs). Restarting/stopping the task clears job history.
- Job status values are `queued`, `running`, `completed`, and `failed`.

Endpoints
---------
### GET /servers
Lists SAS servers visible to Enterprise Guide.

**Request**
```
curl http://127.0.0.1:17832/servers
```

**Response**
```json
[
  { "name": "SASApp", "isassigned": true },
  { "name": "SASAppVA", "isassigned": false }
]
```

### GET /servers/{server}/libraries
Lists libraries on the specified server.

**Request**
```
curl http://127.0.0.1:17832/servers/SASApp/libraries
```

**Response**
```json
[
  { "name": "SASHELP", "libref": "SASHELP", "isassigned": true },
  { "name": "WORK", "libref": "WORK", "isassigned": true }
]
```

### GET /servers/{server}/libraries/{libref}/datasets
Lists datasets (members) within a library.

**Request**
```
curl http://127.0.0.1:17832/servers/SASApp/libraries/SASHELP/datasets
```

**Response**
```json
[
  { "member": "CLASS", "libref": "SASHELP", "server": "SASApp" },
  { "member": "CARS",  "libref": "SASHELP", "server": "SASApp" }
]
```

### GET /servers/{server}/libraries/{libref}/datasets/{member}/columns
Lists dataset columns (name/label/type/length/format/informat).

**Request**
```
curl http://127.0.0.1:17832/servers/SASApp/libraries/SASHELP/datasets/CLASS/columns
```

**Response**
```json
[
  { "name": "Name", "label": "Name", "type": "Character", "length": 8, "format": "$8.", "informat": "$8." },
  { "name": "Age", "label": "Age", "type": "Numeric", "length": 8, "format": "BEST12.", "informat": "BEST12." }
]
```

### GET /servers/{server}/libraries/{libref}/datasets/{member}/preview
Returns a tabular preview for a dataset by running a short SAS program and parsing preview rows from the log.

**Request**
```
curl "http://127.0.0.1:17832/servers/SASApp/libraries/SASHELP/datasets/CLASS/preview?limit=5"
```

- `limit` is optional (default `20`, max `500`).

**Response**
```json
{
  "server": "SASApp",
  "libref": "SASHELP",
  "member": "CLASS",
  "jobid": "7c5cd4d853814e65b8825386436e18f7",
  "limit": 5,
  "rowcount": 5,
  "columns": [
    { "name": "Name", "label": "Name", "type": "Character", "length": 8, "format": "$8.", "informat": "$8." },
    { "name": "Age", "label": "Age", "type": "Numeric", "length": 8, "format": "BEST12.", "informat": "BEST12." }
  ],
  "rows": [
    { "name": "Alfred", "age": "14" },
    { "name": "Alice", "age": "13" }
  ]
}
```

### POST /datasets/open
Downloads the dataset to a temp directory on the EG workstation and returns its full path.

**Request**
```
POST http://127.0.0.1:17832/datasets/open
Content-Type: application/json

{
  "server": "SASApp",
  "libref": "SASHELP",
  "member": "CLASS",
  "format": "sas7bdat",
  "rowlimit": 500
}
```

- `server`: optional if you rely on EG’s default assigned server.
- `libref`: SAS library name.
- `member`: dataset name within the library.
- `format`: currently ignored; output is always `.sas7bdat`.
- `rowlimit`: currently ignored; the entire dataset is downloaded.

**Response**
```json
{
  "path": "C:\\Users\\you\\AppData\\Local\\Temp\\SDVBridge\\SASApp\\SASHELP\\CLASS.sas7bdat",
  "filename": "CLASS.sas7bdat"
}
```

Delete exported files when you are done with them. Subsequent calls reuse the same server/libref folders and overwrite the `member.sas7bdat` file.

### POST /programs/submit
Submits SAS code via the active EG session and returns a job id.

**Request**
```
POST http://127.0.0.1:17832/programs/submit
Content-Type: application/json

{
  "server": "SASApp",
  "serverlogpath": "/saswork/logs/sdvbridge_submit.log",
  "serveroutputpath": "/saswork/logs/sdvbridge_submit.lst",
  "code": "proc print data=sashelp.class(obs=5); run;"
}
```

- `server`: optional; if omitted SDVBridge uses the consumer assigned/default server.
- `serverlogpath`: optional; server-side path for captured SAS log.
- `serveroutputpath`: optional; server-side path for captured SAS listing/output.
  - `serverlogpath` and `serveroutputpath` must be provided together.
- `code`: SAS program text to submit.
- If the two server path fields are omitted, SDVBridge falls back to its automatic capture strategy.

**Response**
```json
{
  "jobid": "3f2d89ca8ff940f38fe2f53f76075859",
  "status": "completed",
  "submittedat": "2026-02-08T20:58:30.9812151+00:00",
  "startedat": "2026-02-08T20:58:30.9812151+00:00",
  "completedat": "2026-02-08T20:58:31.2173404+00:00"
}
```

If submit fails, the response still includes `jobid` with `status: "failed"` and an `error` message.

### POST /programs/submit/async
Queues SAS code and returns immediately with a job id.

**Request**
```
POST http://127.0.0.1:17832/programs/submit/async
Content-Type: application/json

{
  "server": "SASApp",
  "serverlogpath": "/saswork/logs/sdvbridge_submit.log",
  "serveroutputpath": "/saswork/logs/sdvbridge_submit.lst",
  "code": "proc means data=sashelp.class; var height weight; run;"
}
```

**Response** (`202 Accepted`)
```json
{
  "jobid": "6d8f07af8c8e4f3087d0eab26fe0f3b4",
  "status": "queued",
  "submittedat": "2026-02-09T01:11:42.1040073+00:00"
}
```

### GET /jobs/{jobid}
Returns current job state and timestamps.

**Request**
```
curl http://127.0.0.1:17832/jobs/6d8f07af8c8e4f3087d0eab26fe0f3b4
```

**Response (running)**
```json
{
  "jobid": "6d8f07af8c8e4f3087d0eab26fe0f3b4",
  "status": "running",
  "submittedat": "2026-02-09T01:11:42.1040073+00:00",
  "startedat": "2026-02-09T01:11:42.1873384+00:00"
}
```

**Response (completed)**
```json
{
  "jobid": "6d8f07af8c8e4f3087d0eab26fe0f3b4",
  "status": "completed",
  "submittedat": "2026-02-09T01:11:42.1040073+00:00",
  "startedat": "2026-02-09T01:11:42.1873384+00:00",
  "completedat": "2026-02-09T01:11:42.9300222+00:00"
}
```

### GET /jobs/{jobid}/artifacts
Returns generated result artifact metadata (local files produced by the job).

**Request**
```
curl http://127.0.0.1:17832/jobs/6d8f07af8c8e4f3087d0eab26fe0f3b4/artifacts
```

**Response**
```json
{
  "jobid": "6d8f07af8c8e4f3087d0eab26fe0f3b4",
  "status": "completed",
  "artifacts": [
    {
      "id": "e7992f08da9948ad9722f714ab027f4c",
      "name": "result.html",
      "path": "C:\\Users\\you\\AppData\\Local\\Temp\\SDVBridge\\Jobs\\6d8f07...\\Artifacts\\result.html",
      "contenttype": "text/html",
      "sizebytes": 1861
    },
    {
      "id": "f4f9f7ec3c7f4bc8ac5f7c36512012be",
      "name": "result.pdf",
      "path": "C:\\Users\\you\\AppData\\Local\\Temp\\SDVBridge\\Jobs\\6d8f07...\\Artifacts\\result.pdf",
      "contenttype": "application/pdf",
      "sizebytes": 23104
    }
  ]
}
```

Artifacts are written under `%TEMP%\SDVBridge\Jobs\<jobid>\Artifacts`.

### GET /jobs/{jobid}/artifacts/{artifactid}
Downloads a specific artifact file by id (or by artifact filename).

**Request**
```
curl -L "http://127.0.0.1:17832/jobs/6d8f07af8c8e4f3087d0eab26fe0f3b4/artifacts/e7992f08da9948ad9722f714ab027f4c" -o result.html
```

**Response**
- Binary file stream with `Content-Type` based on artifact type.
- `Content-Disposition: attachment; filename="<artifact name>"`.

### GET /jobs/{jobid}/log
Returns the SAS log captured for a previously submitted job.

- Optional query: `offset=<n>` returns only newly appended characters from the requested offset.
- Response includes `nextoffset`; use it in the next poll request.

**Request**
```
curl http://127.0.0.1:17832/jobs/3f2d89ca8ff940f38fe2f53f76075859/log
```

**Incremental polling example**
```
curl "http://127.0.0.1:17832/jobs/3f2d89ca8ff940f38fe2f53f76075859/log?offset=1200"
```

**Response**
```json
{
  "jobid": "3f2d89ca8ff940f38fe2f53f76075859",
  "status": "completed",
  "log": "NOTE: There were 19 observations read from the data set SASHELP.CLASS.\n...",
  "offset": 1200,
  "nextoffset": 1384,
  "iscomplete": true
}
```

### GET /jobs/{jobid}/output
Returns listing/output text captured for a previously submitted job.

**Request**
```
curl http://127.0.0.1:17832/jobs/3f2d89ca8ff940f38fe2f53f76075859/output
```

**Response**
```json
{
  "jobid": "3f2d89ca8ff940f38fe2f53f76075859",
  "status": "completed",
  "output": "Obs Name Sex Age Height Weight\n1 Alfred M 14 69.0 112.5\n..."
}
```

Client Workflow Examples
------------------------
Dataset browse/download:
1. `GET /servers` → pick server.
2. `GET /servers/{server}/libraries` → pick libref.
3. `GET /servers/{server}/libraries/{libref}/datasets` → pick dataset.
4. `GET /servers/{server}/libraries/{libref}/datasets/{member}/columns` (optional, inspect schema first).
5. `GET /servers/{server}/libraries/{libref}/datasets/{member}/preview?limit=20` (optional, inspect sample rows).
6. `POST /datasets/open` to create a `.sas7bdat` copy and retrieve its local file path.

Program execution:
1. `POST /programs/submit/async` with SAS code.
2. Read `jobid` from the response.
3. Poll `GET /jobs/{jobid}` until `status` becomes `completed` or `failed`.
4. Poll `GET /jobs/{jobid}/log?offset=<n>` for near real-time log updates (`n` starts at `0`, then use `nextoffset`).
5. `GET /jobs/{jobid}/artifacts` to list HTML/PDF/Excel-style results.
6. `GET /jobs/{jobid}/artifacts/{artifactid}` to download selected artifact files.
7. `GET /jobs/{jobid}/output` for listing/output text.

Any HTTP-capable client (curl, Python, .NET, etc.) can follow these sequences to explore metadata, export datasets, and run SAS code through the SDVBridge REST API.
