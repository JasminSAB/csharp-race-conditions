# Race Conditions in C#, Reproduced on Every Run

Source code for the dotnetleet.com post
[Race Conditions in C#: The Test You Have Not Written Yet](https://dotnetleet.com/Blogs/PreventRaceConditions).

Six race conditions from ordinary backend code. For each one there is a naive implementation, a fix, and
two tests: one that reproduces the race on **every** run, and the same test against the fix.

The trick is a **gate**: a small helper that holds every caller at one point until all of them have
arrived. Put it between the check and the act, and two callers interleave there every time, instead of
once in a thousand runs.

## Requirements

- .NET 10 SDK or later ([download](https://dotnet.microsoft.com/download/dotnet/10.0))

## Run

```
dotnet test
dotnet run -c Release --project tools/RaceLab
```

## The six races

| # | Race | Naive code | Fix | Folder |
|---|---|---|---|---|
| 1 | Lost increment | `_views++` on a shared counter | `Interlocked.Increment` | `src/RaceConditions/1-Counter` |
| 2 | Read, await, write (no extra thread needed) | read points, `await`, save points + 50 | `SemaphoreSlim` around the whole step (`lock` cannot contain `await`) | `2-ReadAwaitWrite` |
| 3 | Check-then-act across `await` | "not redeemed yet?", grant, mark redeemed | claim first with `ConcurrentDictionary.TryAdd` | `3-CheckThenAct` |
| 4 | `GetOrAdd` runs the factory twice | `GetOrAdd(key, Load)` on an expensive call | store `Lazy<T>` values | `4-GetOrAdd` |
| 5 | `Lazy<Task<T>>` caches a failure | one timeout at startup, every later call fails | forget a faulted task, retry on the next call | `5-LazyTask` |
| 6 | Two instances, one database | in-process claim plus "already redeemed?" query | unique index in the database | `6-TwoInstances` |

`dotnet test` (13 tests) passes on every run: the "race reproduced" tests prove the naive code loses
data, the "fix holds" tests prove the fix does not. 30 consecutive runs, 0 failures.

## How often an ordinary test catches the race

`tools/RaceLab` runs the naive code the way a typical test does (start the calls in parallel, check the
result, no gate) 1,000 times per experiment. Median of three runs, .NET 10, 32 logical cores:

| Experiment | Race caught |
|---|---|
| `_views++`, 2 threads x 1,000 increments | 979 / 1,000 |
| Read-await-write, in-memory fake that completes synchronously | 0 / 1,000 |
| Read-await-write, fake with one `await Task.Yield()` | 857 / 1,000 |
| Coupon check-then-act, in-memory fake that completes synchronously | 0 / 1,000 |
| Coupon check-then-act, fake with one `await Task.Yield()` | 947 / 1,000 |
| `GetOrAdd`, two threads | 6 / 1,000 |

The usual in-memory fake never lets the race happen, because its tasks complete synchronously. A real
await makes the test flaky. The gated tests catch every race on every run.

## Files

| File | What it shows |
|---|---|
| `src/RaceConditions/*` | One folder per race: the naive class and the fixed class side by side |
| `tests/RaceConditions.Tests/Gate.cs` | The gate that forces two callers to interleave |
| `tests/RaceConditions.Tests/RaceTests.cs` | "Race reproduced" and "fix holds" for each race |
| `tools/RaceLab/Program.cs` | The detection-rate experiment |
