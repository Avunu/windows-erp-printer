# Setting up the destination

Each printer profile uses one backend. Always use a dedicated integration account with only the access it needs. Its key sits on every machine that runs ERP Printer.

## Odoo 19+ (`OdooJson2`)

Uses the external JSON-2 API: `POST /json/2/<model>/create` with `Authorization: bearer <key>`.

1. Create an internal user, for example `ERP Printer`, with access to Documents (or to the model you attach to).
2. As that user, go to **Preferences > Account Security > New API Key**.
3. In the profile, set **Server URL** (`https://yourco.odoo.com`), the **API key**, and **Database** if the server hosts more than one.
4. Choose the destination:
   - **documents.document** (Documents app, Enterprise): set **Documents folder ID**. Open the folder in Odoo; the id is in the URL, or under *Settings > Technical > Models > documents.document*.
   - **ir.attachment** (works on Community): optionally set **Attach to model**/**Attach to record ID** to land the file on a specific record, such as a "Scanned inbox" `res.partner` or a project.
5. Optional: **Map Windows users to Odoo users** sets `owner_id` on `documents.document`. The printing user is looked up in `res.users` by login or email. Candidates are the Windows user name, `<user>@<User email domain>`, and the user's AD `mail` and `userPrincipalName`. Results are cached for a day, and misses for an hour.
6. Optional: **Extra fields (JSON)** is merged into the created record, with template tokens expanded. For example:

   ```json
   {"description": "Printed by {user} on {computer}", "tag_ids": [[6, 0, [3]]]}
   ```

Field names on `documents.document` changed between Odoo versions (folders became documents in 18). Check your version's model before relying on custom fields.

## Odoo 14-18 (`OdooJsonRpc`)

The same destinations, over `/jsonrpc` with `execute_kw`. It also needs **Login** (the integration user's login) because the external API authenticates with login + API key. If **Database** is empty and the server lists exactly one database, that one is used.

Odoo is phasing this API out in favour of JSON-2. Switch the profile's backend to `OdooJson2` when you upgrade to 19.

## ERPNext / Frappe (`ERPNext`)

Uses `POST /api/method/upload_file` (multipart, streamed from disk) with `Authorization: token <key>:<secret>`.

1. Create a user for the integration with permission to create **File** records (and to write to the DocType you attach to).
2. On that user, **Settings > API Access > Generate Keys**. Copy the API key and the secret.
3. In the profile, set **Server URL**, **API key** and **API secret**.
4. Choose where files go:
   - **File folder**: an existing folder in the File Manager, such as `Home` or `Home/Scans`.
   - **Attach to DocType/document**: attach to a specific document. The document name supports tokens, e.g. `{title}` if users print documents named after the ERPNext record.
   - **Private file**: on by default.

## Webhook (`Webhook`)

POSTs every document to **Server URL**:

- **Multipart** (default): a file part (field name configurable, default `file`) plus form fields `id`, `fileName`, `title`, `user`, `computer`, `printed`, `jobId`, `pages`, `profile`, and `metadata` (all of these as JSON).
- **Json**: `{"contentType": "application/pdf", "contentBase64": "...", "metadata": {...}}`.

Headers: `X-ErpPrinter-Event: document` (or `test` from **Test connection**), and `Idempotency-Key: <document id>`, which stays the same across retries so you can de-duplicate. If an API key is set, it is sent as the value of **Auth header name** (default `Authorization`, so enter e.g. `Bearer xyz` as the key).

Any 2xx is success. If the response is JSON with an `id`, that id is recorded as the remote id. Errors are classified the same way as for the other backends: 4xx other than 401/403/408/429 fail immediately; everything else is retried.

Examples:

- **Paperless-ngx:** URL `https://paperless.example.com/api/documents/post_document/`, file field `document`, auth header `Authorization`, key `Token <your token>`.
- **n8n / Make / Power Automate:** point the URL at a webhook trigger and read the `metadata` field.

## Adding a backend

See [development.md](development.md#adding-a-backend). A backend is a single file with two functions.
