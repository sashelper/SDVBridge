SDVBridge REST API Guide
=============================

Starting the Server
-------------------
1. Open SAS Enterprise Guide.
2. Launch **Tasks ▸ SDVBridge Samples ▸ REST Server Control**.
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

Client Workflow Example
-----------------------
1. `GET /servers` → pick server.
2. `GET /servers/{server}/libraries` → pick libref.
3. `GET /servers/{server}/libraries/{libref}/datasets` → pick dataset.
4. `POST /datasets/open` to create a `.sas7bdat` copy and retrieve its local file path.

Any HTTP-capable client (curl, Python, .NET, etc.) can follow this sequence to explore SAS metadata and pull down datasets through the SDVBridge REST API.
