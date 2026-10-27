# CMS event service

ASP.NET Core + EF Core + PostgreSQL implementation of the Lateral Group assessment. Two projects: the API (including application logic and persistence) and xUnit tests. No queue or generic repository. All webhook work runs in the request with asynchronous database I/O; success means the transaction committed.

## Run on Windows or macOS

Prerequisites: Docker Desktop with Linux containers and Compose v2, Python 3.10+, and (for local builds/tests) .NET SDK **9.0.315**. `global.json` pins the SDK feature band and allows newer patches. .NET 9 satisfies the assignment and matches the available SDK. It reaches end of support on **November 10, 2026**; migrate to .NET 10 LTS before operating beyond that date ([Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)). The Docker runtime uses the serviced `9.0` tag; images are not digest-pinned.

From the repository root:

```powershell
# Windows (PowerShell)
py -3 scripts/setup.py
docker compose up --build -d
Invoke-RestMethod http://localhost:8080/health/ready
py -3 scripts/smoke.py
```

```bash
# macOS
python3 scripts/setup.py
docker compose up --build -d
curl --fail http://localhost:8080/health/ready
python3 scripts/smoke.py
```

Allow startup to finish before running the readiness/smoke commands. Compose waits for PostgreSQL readiness, runs the one-shot migration service, then starts the API. `docker compose ps -a` and `docker compose logs migrate api` help diagnose startup. `docker compose down` preserves data; `docker compose down -v` **deletes development database data**.

`setup.py` creates an ignored `.env` with distinct random credentials and a GUID CMS password. It never prints secrets or overwrites an existing file. Files are created with owner-only permissions on POSIX; on Windows, keep the repository in your private user directory with appropriate ACLs. Do not commit `.env` or paste `docker compose config` output (it contains secrets). `.env.example` has placeholders only.

## Local .NET development and migrations

```text
dotnet restore
dotnet build --no-restore
docker compose up -d db
```

Then run `py -3 scripts/dev.py migrate` and `py -3 scripts/dev.py run` on Windows; use `python3` for the same commands on macOS. In another terminal run `scripts/smoke.py`. `dev.py` loads `.env` and connects to the published localhost database port. Existing environment variables override `.env` values. Set `ConnectionStrings__Writer` for an existing PostgreSQL database, or `POSTGRES_PORT` before Compose startup if 5432 is occupied.

The explicit migration entry point is `dotnet run --project src/CmsEventService -- --migrate` (requires `ConnectionStrings__Writer`, but not API credentials). It applies committed EF migrations and exits. Normal API startup does not migrate. Run only one migration job at a time and use a migration identity with DDL privileges; the runtime identity needs table DML privileges. For new migrations:

```text
dotnet tool restore
dotnet ef migrations add DescriptiveName --project src/CmsEventService --context WriterContext
dotnet ef migrations has-pending-model-changes --project src/CmsEventService --context WriterContext
```

The design-time factory's placeholder connection supports generating migrations offline; it is not a working credential. To apply through EF tooling, supply a real environment connection string. Secrets may alternatively be set with `dotnet user-secrets --project src/CmsEventService` during Development.

## API and authentication

| Endpoint | Identity | Behavior |
| --- | --- | --- |
| `POST /cms/events` | CMS only | JSON array of 1–100 events; transactional processing |
| `GET /entities?offset=0&limit=20` | Consumer/admin | Ordered by case-sensitive ID; limit 1–100, offset 0–100000 |
| `GET /entities/{id}` | Consumer/admin | Invisible/missing entities return 404 |
| `POST /entities/{id}/disable` | Admin only | Idempotent local disable; 204, or 404 if absent |
| `GET /openapi/v1.json` | Consumer/admin | OpenAPI document with Basic authentication |
| `GET /health/ready` | Public | Checks that the migrated entity table is queryable |

Basic Authentication uses three distinct configured accounts (`Auth__Cms__Username/Password`, `Auth__Consumer__Username/Password`, `Auth__Admin__Username/Password`). No default credentials exist. Startup checks unique usernames/passwords, a 10–20 character CMS username, a nonempty GUID CMS password, and passwords of at least 16 characters for other accounts. Randomness is provided by setup, not inferred by the validator. Password comparisons use fixed-size hashes and constant-time comparison. Missing or invalid credentials return 401 with a Basic challenge; valid identities with insufficient privileges receive 403. There is no payload edit endpoint or re-enable endpoint.

**HTTPS is required outside local development**: Basic credentials are only Base64 encoded. Terminate TLS at a trusted reverse proxy and prevent public direct HTTP access to Kestrel. Local Compose binds ports to loopback. Use a secret manager/environment injection for deployment, network access controls and a rate-limiting edge for untrusted traffic. These operational controls are not supplied by this assessment.

`scripts/smoke.py` is an executable, cross-platform set of authenticated request examples with synthetic content. It exercises publish, the unpublished next-version case, consumer/admin visibility, disable, republish, list, OpenAPI and hard deletion. It reads credentials without printing them. `CMS_BASE_URL` overrides its default `http://localhost:8080`.

Example webhook body (sent with the CMS Basic identity):

```json
[
  {"type":"publish","id":"X","payload":{"title":"Example"},"version":2,"timestamp":"2024-01-01T00:00:00Z"},
  {"type":"unPublish","id":"X","payload":{"title":"Unpublished revision"},"version":3,"timestamp":"2024-01-01T00:00:01Z"},
  {"type":"delete","id":"Y","timestamp":"2024-01-01T00:00:02Z"}
]
```

## Ordering assumptions and conflict policy

The CMS contract has no globally unique event ID, delete version or sequence number. Assume versions increase for data modifications and timestamps are reliable, nondecreasing UTC instants per entity. First observed versions and version gaps are valid. Do not infer that a revision was previously published. `unPublish` always retains its supplied version and payload, including unknown IDs and the unpublished X+1 scenario.

| Incoming event | Rule |
| --- | --- |
| Version greater than stored version | Apply payload/state unless timestamp is older (409 conflict) |
| Version lower than stored version | Stale; never overwrite data |
| Same version, earlier timestamp | Stale |
| Same version, different payload, timestamp not earlier | Conflict: modifications must increment version |
| Same version/payload, later timestamp | Apply publication transition |
| Same version/payload/timestamp/state | Duplicate |
| Same version/timestamp, different state | Conflict; no arbitrary publish/unpublish tie-break |
| Delete newer than latest entity timestamp | Hard-delete entity row and payload; record deletion barrier |
| Delete older than entity timestamp | Stale |
| Delete ties entity timestamp | Conflict |
| Any event earlier than deletion barrier | Stale, even if version is higher |
| Event ties deletion barrier | Duplicate for delete; conflict for publish/unpublish |
| Event later than barrier, no entity | Allow new lifecycle, including version reset |

Deletion barriers store **only ID and timestamp ticks**, separate from entity rows: no payload, hash or historic versions. They remain even after recreation and are not automatically pruned; pruning requires a CMS delivery/retention guarantee. Timestamps use UTC .NET ticks (`bigint`) so ordering retains all seven fractional digits rather than PostgreSQL timestamp's microsecond precision. Timezones normalize to UTC.

ID reuse assumes old-lifecycle deliveries carry timestamps at or before the deletion. A badly timestamped old event can resurrect an ID; the contract cannot guarantee perfect ordering. A disable flag survives all CMS revisions and transitions within a lifecycle; hard deletion removes it, so a recreated ID starts enabled locally. Disabling an absent ID returns 404 and does not create a future disable reservation.

Conflicts reject the whole current batch and preserve previously committed state. The first accepted event remains if a later contradictory event has identical ordering metadata; the contradiction is surfaced as 409 for source reconciliation, never silently resolved by arrival order. No complete event history is stored, so older retries classify as stale rather than duplicate once newer metadata is present.

## Batch semantics, validation and concurrency

Parse and validate the **entire** batch before opening a transaction. Any invalid event yields 400 with zero-based input indices; malformed JSON/root-level errors use index -1. Oversized requests return 413; non-JSON content returns 415. Valid batches execute in input order. `applied`, `duplicate`, `stale`, `conflicting` outcomes preserve indices. On any conflict the response is 409 with `committed:false`; tentative applied results become `rolledBack`. Other results describe evaluation against the tentative input sequence. On success return 200 and `committed:true`, including batches containing only stale/duplicate events. Infrastructure failures return 503 with correlation information and no success assertion; clients may safely retry. A connection loss during commit can leave the commit outcome unknown, as with any request/transaction boundary.

Envelope fields are case-sensitive; unknown fields and duplicate JSON names (at **any depth**) are rejected. IDs are trimmed and must match `[A-Za-z0-9][A-Za-z0-9._:-]{0,127}`. Types are exactly `publish`, `unPublish`, `delete`. Versioned events require a positive signed 64-bit version, RFC3339 timestamp after Unix epoch with explicit timezone (up to seven fractional digits), and arbitrary object payload. Delete must omit payload and version. Future timestamps are not compared with the server clock: reliable CMS time is an explicit assumption.

Limits: 1 MiB request, 100 events, 64 KiB per raw payload, JSON depth 32. PostgreSQL-incompatible null characters are rejected. Payload numbers allow up to 1000 significant digits and normalized decimal exponent magnitude 1000. Object key order and numeric spellings normalize for idempotency; arrays remain ordered and payload string values are preserved, including HTML. Consumers must perform context-appropriate output encoding. No indiscriminate HTML stripping occurs.

All entity mutations take **transaction-scoped PostgreSQL advisory locks** on stable SHA-256-derived 64-bit ID keys, acquired in sorted numeric order for the whole batch. This covers concurrent creation (where no row exists), updates, deletion and admin disable, across processes. Hash collisions only reduce concurrency. READ COMMITTED plus these locks ensures each writer sees the previous committed state; direct external database writers must follow the same protocol. Changes flush inside one transaction to support repeated IDs but are acknowledged/logged as applied only after commit. Long/overlapping batches increase latency; bounded inputs and Npgsql command timeouts limit work. No process-local mutex or fire-and-forget queue is used.

Reads use a separate no-tracking context with blocked `SaveChanges` and a PostgreSQL read-only session default. Queries filter visibility, project columns, order and paginate in SQL. Optionally set `ConnectionStrings__Reader` to a SELECT-only database role for privilege-level enforcement (the application session setting is not a defense against malicious application code). Both contexts can connect to the same instance; this is **not a physical read replica**. The visibility index supports publication/disable filtering. Offset pagination is bounded but does not offer a snapshot across changing pages.

Structured application logs include correlation ID, event index, safe ID/type/version, outcome and fixed reason. They do not log bodies, credentials or Authorization headers. EF command/query/update logs are disabled because provider errors can include submitted values. Rolled-back events are never logged as committed. Do not enable sensitive EF logging in deployment.

## Tests

```text
dotnet test
```

By default, HTTP integration tests start a disposable PostgreSQL 17 container through Testcontainers. Docker must be running. They apply the real EF migrations; they do not use EF InMemory. If Docker is unavailable, set `TEST_POSTGRES_CONNECTION` to a dedicated PostgreSQL server connection with permission to create/drop databases. The fixture creates a random isolated test database and removes it afterward. Never point this at a production server. Example with placeholders:

```powershell
$env:TEST_POSTGRES_CONNECTION = 'Host=localhost;Port=<port>;Database=postgres;Username=<test-user>;Password=<test-password>'
dotnet test
```

```bash
export TEST_POSTGRES_CONNECTION='Host=localhost;Port=<port>;Database=postgres;Username=<test-user>;Password=<test-password>'
dotnet test
```

Unit-only: `dotnet test --filter FullyQualifiedName~PolicyTests`. Tests cover ordering, first/later/gapped revisions, retained unpublished data, transitions, retries/conflicts, validation atomicity, deletion/reuse, visibility, Basic parsing and role boundaries, no-write reads, concurrent requests and lock ordering. CI runs restore/build/tests on Linux with Testcontainers, then builds Compose and executes the same smoke flow.

## Interview decisions and limitations

- Synchronous processing gives immediate durable acknowledgement and simple failure semantics. It holds HTTP requests during database work and has lower burst tolerance than a durable queue; a future queue would require durable ingestion and a changed acknowledgement contract.
- Version and timestamp solve different ordering problems. Delete's missing version forces a documented timestamp assumption and a separate tombstone barrier.
- Publication and local disable are independent: visibility is `Published && !LocallyDisabled` for consumers; admin uses the same endpoints without that filter.
- Database locks protect absent rows as well as existing ones and coordinate multiple service instances. Bounded atomic batches deliberately trade throughput for straightforward correctness.
- Basic authentication matches the assessment; this is not a multi-user credential store. Secrets rotate through configuration/restart. No audit-history database, automatic conflict reconciliation, tombstone retention policy, performance benchmark or external deployment is included.

## Verification record (October 27, 2026)

- Restore and Release build succeeded with zero warnings/errors.
- All 55 tests passed against an isolated real PostgreSQL 17 database on Windows; no skipped tests.
- EF reported no pending model changes. The documented migration command applied the schema to a fresh smoke database.
- The documented HTTP smoke script passed all 15 requests against Kestrel, including the unpublished revision and local disable flow.
- `docker compose config --quiet` and `git diff --check` passed.
- Local Docker Desktop returned HTTP 500 from its engine API. Local image build, Compose startup and the Testcontainers startup path could not be executed here. The integration suite used its documented external-PostgreSQL option instead. Linux CI is configured to exercise Testcontainers and Compose; macOS execution has not been verified locally.
