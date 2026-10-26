# CalcApp — calculator + load / stress test bench (ASP.NET Core 8, Linux, Docker)

A web calculator whose page also has buttons to **load-test** and **stress** the server,
plus a live view of CPU, memory and requests.

- Built for **.NET 8** (`net8.0`). `RollForward=LatestMajor`, so it also runs on .NET 9 / 10.
- Runs as a non-root user (`app`, UID 1654) on port **8080** inside the container.
- No NuGet packages, no database.

## Quick start on the Linux VM

Copy this folder to the VM, then in the folder:

```bash
docker compose up -d --build
```

Open `http://<vm-ip>:8080`. Docker shows the container as `healthy` after a few seconds:

```bash
docker ps                      # STATUS: Up ... (healthy)
docker logs -f calcapp         # app log
docker compose down            # stop and remove
```

### Without docker compose

```bash
docker build -f Dockerfile.prebuilt -t calcapp .
docker run -d --name calcapp -p 8080:8080 --restart unless-stopped --cpus 2 --memory 2g calcapp
```

### Two Dockerfiles

| File | What it does | Images it pulls |
|---|---|---|
| `Dockerfile.prebuilt` (used by compose) | Copies the ready-built app from `publish/` | `mcr.microsoft.com/dotnet/aspnet:8.0` (~90 MB compressed) |
| `Dockerfile` | Builds from `src/` inside Docker | `aspnet:8.0` **and** `sdk:8.0` (~300 MB compressed) |

To build from source with compose, set `dockerfile: Dockerfile` in `docker-compose.yml`.
The source build also works when nuget.org is unreachable, because the app uses no NuGet packages.

### Other .NET versions or a registry mirror

The base images are build arguments:

```bash
# run on .NET 10
docker build -f Dockerfile.prebuilt --build-arg RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0 -t calcapp .

# if the VM cannot reach mcr.microsoft.com, use your mirror / private registry
docker build -f Dockerfile.prebuilt --build-arg RUNTIME_IMAGE=<your-registry>/dotnet/aspnet:8.0 -t calcapp .
```

Or set `args: RUNTIME_IMAGE: ...` in `docker-compose.yml`.

## What is on the page

**Calculator** — tap the keys or type on the keyboard (Enter `=`, Esc clears, Backspace deletes).
Supports `+ − × ÷ mod ^ ! ( )`, `sqrt sin cos tan ln log abs exp round floor ceil`, `pi`, `e`, and `1.5e3` notation.
Every result is calculated on the server (`POST /api/calc/eval`).

**Load test** — the server sends real HTTP requests to its own API over loopback, so each call is a real
web request (visible in logs and APM tools such as AppDynamics). Choose:

| Traffic | Endpoint(s) |
|---|---|
| Calculator requests | `GET /api/calc?a=&b=&op=` |
| Expression requests | `POST /api/calc/eval` |
| Mixed traffic | 50% calc, 25% eval, 12% slow, 10% CPU, 3% errors |
| Slow responses | `GET /api/slow?ms=100..500` |
| CPU-heavy requests | `GET /api/stress/cpu-work?ms=20..80` |
| Failing requests | `GET /api/error` (HTTP 500) |

Stop after a number of requests (max 1,000,000) or a length of time (max 600 s), with up to 500 parallel users.
Results: completed, errors, requests/s, average, median, 95th / 99th percentile, slowest.

**Stress the server** — CPU (N busy threads, max 4 × CPUs) and memory (allocates and holds N MB, max 4096).
Jobs stop on their own (max 300 s) or with *Stop all stress*. If the container memory limit is reached,
the app reports it, frees the memory and keeps running.

**Server right now** — CPU %, requests/s (last 2 minutes), memory, heap limit, threads, requests and errors per endpoint.

## API

| Method | Path | Notes |
|---|---|---|
| GET | `/api/calc?a=5&b=3&op=add` | ops: `add sub mul div pow mod` |
| POST | `/api/calc/eval` | body `{"expression":"(2+3)*sqrt(16)"}` |
| POST | `/api/loadtest/start` | body `{"target":"mixed","requests":2000,"concurrency":20}` or `{"target":"calc","durationSeconds":60,"concurrency":50}` |
| GET | `/api/loadtest/status` | progress and latency figures |
| POST | `/api/loadtest/stop` | |
| POST | `/api/stress/cpu?threads=2&seconds=30` | |
| POST | `/api/stress/memory?mb=256&seconds=30` | |
| POST | `/api/stress/stop` | stops CPU and memory jobs |
| GET | `/api/stress/status` | |
| GET | `/api/slow?ms=500` | waits, then answers (max 30000) |
| GET | `/api/error?code=500` | simulated error |
| GET | `/api/stress/cpu-work?ms=50` | CPU work inside one request (max 5000) |
| GET | `/api/stats` · POST `/api/stats/reset` | live metrics |
| GET | `/health` | used by the Docker HEALTHCHECK |

Example from the VM shell:

```bash
curl -s -X POST localhost:8080/api/loadtest/start -H 'Content-Type: application/json' \
     -d '{"target":"mixed","durationSeconds":120,"concurrency":50}'
curl -s localhost:8080/api/loadtest/status
```

## Run without Docker (any Linux with the ASP.NET Core 8+ runtime)

```bash
cd publish
ASPNETCORE_HTTP_PORTS=8080 dotnet CalcApp.dll
```

## Rebuild the publish folder

```bash
dotnet publish src/CalcApp/CalcApp.csproj -c Release -o publish
```

## Files

```
Dockerfile            build from source (multi-stage)
Dockerfile.prebuilt   use publish/ (default for compose)
docker-compose.yml    port 8080, restart policy, CPU/memory limits
publish/              ready-built app
src/CalcApp/          source code
```
