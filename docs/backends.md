# Setting up the destination

Each printer profile uses one backend. Always use a dedicated integration account with only the access it needs. Its key sits on every machine that runs ERP Printer.

## Odoo 19+ (`OdooJson2`)

Uses the external JSON-2 API: `POST /json/2/<model>/create` with `Authorization: bearer <key>`.

1.  Create an internal user, for example `ERP Printer`, with access to create records in the model you target.
    
2.  As that user, go to **Preferences > Account Security > New API Key**.
    
3.  In the profile, set **Server URL** (`https://yourco.odoo.com`), the **API key**, and **Database** if the server hosts more than one.
    
4.  Choose the destination. It works like the alias of an Odoo mail gateway: a profile names a **model** and its **default values**, and every printed document creates one record of that model.
    
    | Setting | Meaning | Default |
    | --- | --- | --- |
    | Odoo model | Technical name of any model the API user may create records in, such as ir.attachment, documents.document, edi.exchange.record or one of your own. | ir.attachment |
    | File field | Binary field that receives the PDF, sent base64-encoded. | datas |
    | File name field | Field that receives the file name from File name template. Leave it empty for a model without one. | name |
    | Default values (JSON) | A JSON object of field values for every record, for example {"router_id": 1}. See below. | empty |
    
5.  Optional: **Map Windows users to Odoo users** writes the printing user to the **User field** (default `owner_id`; use `user_id` on most other models). The user is looked up in `res.users` by login or email. Candidates are the Windows user name, `<user>@<User email domain>`, and the user's AD `mail` and `userPrincipalName`. Results are cached for a day, and misses for an hour.
    
6.  Click **Test connection**. Besides reaching the model, it checks that the file field is a binary field and that every field you named (file name, user, default values) exists on the model, so a typo shows up before the first print.
    

### Default values

**Default values** is a JSON object, merged into every created record, with `{token}` placeholders expanded in strings (see `FileNameTemplate` for the list). Write values the way the Odoo API expects them: a number for a many2one id, `[[6, 0, [3, 4]]]` for a many2many replace command, `true`/`false` for booleans.

```json
{"description": "Printed by {user} on {computer}", "tag_ids": [[6, 0, [3]]]}
```

Order of precedence: the file name and mapped user are set first, then the default values on top (so `{"name": "Scan {date}"}` replaces the file name), and the file field is set last. The PDF can never be replaced by a default value.

### Recipes

| Goal | Odoo model | File field | File name field | Default values |
| --- | --- | --- | --- | --- |
| Plain attachment | ir.attachment | datas | name | (empty) |
| Attach to a record, such as a "Scanned inbox" partner | ir.attachment | datas | name | {"res_model": "res.partner", "res_id": 3} |
| Documents app (Enterprise) | documents.document | datas | name | {"folder_id": 12} |
| OCA EDI exchange record | edi.exchange.record | exchange_file | exchange_filename | {"backend_id": 3, "type_id": 8, "edi_exchange_state": "input_received"} |
| A custom import model | x_import.document | its binary field | its name field | {"router_id": 1} |

Field names on `documents.document` changed between Odoo versions (folders became documents in 18). Check your version's model before relying on custom fields. To find a model's or field's technical name, turn on developer mode and look under _Settings > Technical > Models_.

### EDI exchange records

With the `edi.exchange.record` recipe above, every printed document becomes an inbound exchange record of the [OCA EDI framework](https://github.com/OCA/edi-framework). It then shows up under _EDI > Exchange records_, where it can be processed, retried and traced like any other exchange, instead of arriving as a bare attachment.

The record is created with the PDF as its exchange file, the file name from **File name template**, and the state _Received_ (`input_received`): the document has already arrived, so there is nothing left for a receive step to fetch. From there the backend processes it as usual: immediately during the upload if the exchange type has **Quick execution** on, otherwise on the next run of the EDI process cron.

In Odoo:

1.  Create (or reuse) an `edi.backend`. Its id is in the URL of its form.
2.  Create an `edi.exchange.type` with direction **Input** and a **Code**, either on that backend or on its backend type with no backend set. Choose a **Processor** for the PDF, for example one that runs a `base.import.pdf.template`. Its id is in the URL of its form.
3.  Give the integration user rights to create EDI exchange records.

Put both ids in **Default values** as `backend_id` and `type_id`. The profile does not look the exchange type up by code, so the id has to be that of an _input_ type that belongs to the backend or to its backend type.

### Upgrading from 0.2

Releases up to 0.2 had fixed settings for three models. They are replaced by the generic ones above; leftover registry values of the old settings are ignored. Because the default model is now `ir.attachment`, a profile that relied on the old default `documents.document` must set **Odoo model** explicitly.

| Old setting | Now |
| --- | --- |
| OdooFolderId = 12 | OdooDefaultValues = {"folder_id": 12} |
| OdooResModel / OdooResId | OdooDefaultValues = {"res_model": "res.partner", "res_id": 3} |
| OdooExtraFields | OdooDefaultValues (same JSON; merge it with the entries above) |
| OdooEdiBackendId / OdooEdiExchangeType | OdooModel = edi.exchange.record, OdooFileField = exchange_file, OdooNameField = exchange_filename, OdooDefaultValues = {"backend_id": 3, "type_id": 8, "edi_exchange_state": "input_received"} (the exchange type is now given by id, not code) |
| UserMapping | unchanged; it now writes to OdooUserField (default owner_id) on any model |

`ir.attachment` records no longer get `mimetype` and `description` set automatically. Odoo derives the mimetype from the file name; add `{"description": "Printed by {user} on {computer}"}` to the default values if you want the description.

## Odoo 14-18 (`OdooJsonRpc`)

The same settings and destinations, over `/jsonrpc` with `execute_kw`. It also needs **Login** (the integration user's login) because the external API authenticates with login + API key. If **Database** is empty and the server lists exactly one database, that one is used.

Odoo is phasing this API out in favour of JSON-2. Switch the profile's backend to `OdooJson2` when you upgrade to 19.

## ERPNext / Frappe (`ERPNext`)

Uses `POST /api/method/upload_file` (multipart, streamed from disk) with `Authorization: token <key>:<secret>`.

1.  Create a user for the integration with permission to create **File** records (and to write to the DocType you attach to).
2.  On that user, **Settings > API Access > Generate Keys**. Copy the API key and the secret.
3.  In the profile, set **Server URL**, **API key** and **API secret**.
4.  Choose where files go:
    -   **File folder**: an existing folder in the File Manager, such as `Home` or `Home/Scans`.
    -   **Attach to DocType/document**: attach to a specific document. The document name supports tokens, e.g. `{title}` if users print documents named after the ERPNext record.
    -   **Private file**: on by default.

## Webhook (`Webhook`)

POSTs every document to **Server URL**:

-   **Multipart** (default): a file part (field name configurable, default `file`) plus form fields `id`, `fileName`, `title`, `user`, `computer`, `printed`, `jobId`, `pages`, `profile`, and `metadata` (all of these as JSON).
-   **Json**: `{"contentType": "application/pdf", "contentBase64": "...", "metadata": {...}}`.

Headers: `X-ErpPrinter-Event: document` (or `test` from **Test connection**), and `Idempotency-Key: <document id>`, which stays the same across retries so you can de-duplicate. If an API key is set, it is sent as the value of **Auth header name** (default `Authorization`, so enter e.g. `Bearer xyz` as the key).

Any 2xx is success. If the response is JSON with an `id`, that id is recorded as the remote id. Errors are classified the same way as for the other backends: 4xx other than 401/403/408/429 fail immediately; everything else is retried.

Examples:

-   **Paperless-ngx:** URL `https://paperless.example.com/api/documents/post_document/`, file field `document`, auth header `Authorization`, key `Token <your token>`.
-   **n8n / Make / Power Automate:** point the URL at a webhook trigger and read the `metadata` field.

## Adding a backend

See [development.md](development.md#adding-a-backend). A backend is a single file with two functions.
