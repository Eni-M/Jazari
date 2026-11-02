# Static-analysis pre-pass for /review: scanners find, the small model triages and explains

Research date: 2026-11-02. This note builds on `research_notes/Whole laptop Jazari optimization/repo_review.md`, which already covers the tool table (gitleaks, Opengrep, osv-scanner, NetAnalyzers, SecurityCodeScan, Semgrep CE, CodeQL licence), the basic commands, the ZeroFalse, IRIS, Memoir and Sifting-the-Noise results, the risk-score formula and the JSONL layout. Those points are not repeated here. This note adds what changed or was missing, and turns it into a design that fits `src/MyOwnLLM.Cli/ProjectReview.cs`.

Evidence labels (same as repo_review.md, plus one):
- **[M]** measured (paper, benchmark, or measurement by the tool's own author, which is noted where it applies).
- **[C]** claimed, with no independent check.
- **[V]** vendor comparison.
- **[2nd]** read through a secondary summary.
- **[S]** search-engine summary only, because the primary page could not be fetched in this session. These hosts were blocked by the session's network policy: `learn.microsoft.com`, `arxiv.org`, `google.github.io`, `bleepingcomputer.com`. Re-check [S] items before relying on them.
- **[E]** my estimate. **[T]** needs a test run on the laptop.

What the current code does (read 2026-11-02): `ProjectReview.RunAsync` loads all files, cuts them into units of about 2,000 tokens, puts each risky file in a unit of its own, and sends every unit with one long checklist prompt (`Instructions`). Findings must quote a real line (`Check` checks this), and results are cached in JSONL by `Hash(PromptVersion + unit text)`. Nothing in the flow uses tool output. Every token of the project is read by the model.

---

## 1. Tool status, licensing, offline use, Windows binaries and speed (late 2026)

### Takeaway
Two things changed since repo_review.md. **Dotnetarium** (Apache-2.0, v2.8.1, 2026-10-19) is a maintained Roslyn taint scanner for ASP.NET Core with SARIF data flows, and its 1.x line was published as `Dotnetarium.Analyzers.SCS`, so it is the practical successor to the stale SecurityCodeScan. **gitleaks** is now feature-frozen in favour of Betterleaks, but Betterleaks v2 is not yet stable and documents no Windows binary or SARIF output. Use these core tools: Roslyn CA security rules, Dotnetarium, gitleaks, osv-scanner and Opengrep with Jazari's own rules. NuGet audit and DevSkim are optional extras.

### Cited Findings

**.NET-native**
- CA3001 (SQL), CA3003 (file path), CA3007 (open redirect) and CA5391 (antiforgery in MVC controllers) are all "Enabled by default in .NET 10: No". CA3001/3003/3007 have "a configurable limit to how deep this rule will analyze data flow across method calls" — [ca3001.md](https://raw.githubusercontent.com/dotnet/docs/main/docs/fundamentals/code-analysis/quality-rules/ca3001.md), [ca3003.md](https://raw.githubusercontent.com/dotnet/docs/main/docs/fundamentals/code-analysis/quality-rules/ca3003.md), [ca3007.md](https://raw.githubusercontent.com/dotnet/docs/main/docs/fundamentals/code-analysis/quality-rules/ca3007.md), [ca5391.md](https://raw.githubusercontent.com/dotnet/docs/main/docs/fundamentals/code-analysis/quality-rules/ca5391.md). This closes repo_review.md's gap on which CA rules beyond CA3001 use dataflow: at least CA3003 and CA3007 do too.
- The security category holds 94 rules: injection CA3001–CA3012 plus CA2100; deserialization CA2300–CA2330 (BinaryFormatter, LosFormatter, NetDataContractSerializer, `TypeNameHandling` CA2326, insecure `JsonSerializerSettings` CA2327–CA2330); DataSet CA2350–CA2362; web CA3147, CA5363, CA5382/5383 (secure cookies), CA5391, CA5395, CA5396; XML CA3075–CA3077; crypto/TLS/token CA5350–CA5405, including CA5359 (cert validation disabled), CA5390 (hard-coded key), CA5404/CA5405 (token validation disabled); CA5389 (zip-slip). **There is no SSRF rule** — [security-warnings.md](https://raw.githubusercontent.com/dotnet/docs/main/docs/fundamentals/code-analysis/quality-rules/security-warnings.md).
- The whole security category can be switched on with one MSBuild property, `<AnalysisModeSecurity>All</AnalysisModeSecurity>`. This is more precise than `AnalysisMode=All`, which repo_review.md listed as unconfirmed — [msbuild-props.md](https://raw.githubusercontent.com/dotnet/docs/main/docs/core/project-sdk/msbuild-props.md).
- Dataflow rules can be tuned in `.editorconfig`: `dotnet_code_quality.interprocedural_analysis_kind = None|NonContextSensitive|ContextSensitive`, a maximum call-chain length option, and `dotnet_code_quality.dataflow.excluded_symbol_names` for all dataflow rules at once — [Analyzer Configuration.md](https://raw.githubusercontent.com/dotnet/roslyn-analyzers/main/docs/Analyzer%20Configuration.md).
- `Microsoft.CodeAnalysis.NetAnalyzers` on NuGet is at 11.0.100-rc.3 (the .NET 11 previews). The rules also ship inside the SDK — [NuGet flat container](https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.netanalyzers/index.json).
- **Dotnetarium** — [README](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/README.md), [NuGet](https://www.nuget.org/packages/Dotnetarium.Analyzers):
  - Apache-2.0, v2.8.1 dated 2026-10-19, and 14 releases from 2.0.0 to 2.8.1 ([versions](https://api.nuget.org/v3-flatcontainer/dotnetarium/index.json)). It has about 8.1K total downloads and a single listed NuGet owner, `dbalikhin`.
  - Install with `dotnet tool install --global dotnetarium`, then run `dotnetarium MyApp.sln --sarif results.sarif`. The global tool needs the .NET 10 runtime. Projects must be C# on .NET 8/10 or netstandard2.x. VB.NET and .NET Framework are not supported in 2.x.
  - Checks: SQL, OS command, LDAP, XPath and dynamic-code injection; XSS; open redirect; **SSRF**; cookies; TLS certificate validation; file paths; archive extraction; XML; Json.NET type handling; BinaryFormatter; weak crypto; literal secrets (DNA0009, DNA0022); CORS that allows any origin with credentials (DNA0024). Sources are modelled for ASP.NET Core, Minimal APIs, Razor/Blazor, gRPC, SignalR, WebSockets and messaging, and libraries for Dapper and Npgsql. **CSRF is not listed.**
  - `--sarif` writes "SARIF 2.1.0 with relative paths and available data flows". There are fingerprint-based local suppressions in `.dotnetarium/suppressions.sarif`. Secret checking is "otherwise offline" unless `--verify-secrets` is passed.
  - The default **no-build** mode "reconstructs SDK inputs without targets or restore", so no `dotnet build` is needed. Test projects are excluded from solution scans.
  - Upgrading from 1.x means replacing `Dotnetarium.Analyzers.SCS` with `Dotnetarium.Analyzers` and `dotnetarium-scs` with `dotnetarium`.
  - It ships an AI-agent guide: "uncertainty is a reason to investigate, not suppress" — [ai-development.md](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/docs/ai-development.md).
- [M, by the tool author] Dotnetarium wall times on Windows (Ryzen 7 255, 16 logical processors, SDK 10.0.401, all rules, depth 3), single runs with warm caches: LANCommander (21 production compilations) 41.0 s in no-build mode and 76.1 s in project mode at budget 2,500. A small vulnerable demo app took 7.7 s. EF migrations and model snapshots are excluded from taint roots. The tool uses at most 4 compilations at once — [performance-budget-matrix.md](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/docs/performance-budget-matrix.md), [scan-performance.md](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/docs/scan-performance.md).
- SecurityCodeScan.VS2019 is still at 5.6.7, published 2022-09-05, under LGPL-3.0-or-later — [NuGet registration](https://api.nuget.org/v3/registration5-gz-semver2/securitycodescan.vs2019/index.json). Other Roslyn taint options found were RoslynSecurityGuard (last updated 2017) and SmolerSAST 0.4.0 (May 2026, early) — [S] [search summary](https://www.nuget.org/packages/SmolerSAST).
- SonarAnalyzer.CSharp (10.35) is under the **SONAR Source-Available License v1.0** (dated 2024-11-13), not an OSI licence — [LICENSE.txt](https://raw.githubusercontent.com/SonarSource/sonar-dotnet/master/LICENSE.txt).
- **NuGet audit** — [Auditing-Packages.md (docs source)](https://raw.githubusercontent.com/NuGet/docs.microsoft.com-nuget/main/docs/concepts/Auditing-Packages.md):
  - NuGetAudit is on by default for PackageReference from NuGet 6.8 / .NET 8.0.100.
  - "`NuGetAuditMode` defaults to `all` when a project targets `net10.0` or higher", so transitive packages are included. Otherwise it is `direct`. `NuGetAuditLevel` defaults to `low`.
  - Warnings are NU1901–NU1904 (low to critical). **NU1900** is "Error communicating with package source, while getting vulnerability information", which is what an offline restore should give [T]. NU1905 means an audit source has no vulnerability database.
  - nuget.org's data comes from the GitHub Advisory Database. `data.nuget.org` is a vulnerability-only endpoint that can serve as an audit source.
  - Audit sources for `dotnet package list --vulnerable` arrived in .NET 9.0.300, and `dotnet package update --vulnerable` in .NET 10. packages.config audit has existed since NuGet 6.10.

**Pattern / multi-language**
- Opengrep's README shows `opengrep scan --sarif-output=sarif.json -f rules code`, so the flag is **`--sarif-output=`** and not `--sarif -o` as guessed in repo_review.md. It is LGPL-2.1 and a fork of Semgrep 1.100.0. The Windows package holds `opengrep.exe` and its DLLs, which must be kept together. No inter-file taint is mentioned — [repo](https://github.com/opengrep/opengrep).
- Opengrep's C# wiki marks C# 12–14 mostly supported. The gaps: implicit index access and user-defined compound assignment are "supported syntactically, no constant/taint propagation"; overload resolution is partial; and "there is no meaningful way in Opengrep to make fields in a class sources of taint" — [Support for C#](https://github.com/opengrep/opengrep/wiki/Support-for-C%23).
- DevSkim is MIT, installed with `dotnet tool install --global Microsoft.CST.DevSkim.CLI` (latest 1.0.100), and has a Windows binary and SARIF/JSON/text output. The README gives no false-positive or offline statements — [repo](https://github.com/microsoft/DevSkim), [versions](https://api.nuget.org/v3-flatcontainer/microsoft.cst.devskim.cli/index.json).

**Secrets**
- gitleaks README: "Gitleaks is feature complete. I'm not merging new features into Gitleaks. Future releases will be security patches only. I'm shifting my focus to Betterleaks". Report formats are `json, csv, junit, sarif, template` via `-f/--report-format` — [README](https://raw.githubusercontent.com/gitleaks/gitleaks/master/README.md).
- Betterleaks is MIT and "maintained by the folks who made Gitleaks, including the original author". `main` is v2 development, a migration guide covers "CLI, config, report, and SDK changes", and credential validation is opt-in (`-v/--validate`). The README documents no Windows binary and no SARIF output — [repo](https://github.com/betterleaks/betterleaks). [S] It is backed by Aikido Security and was announced in March 2026 — [BleepingComputer](https://bleepingcomputer.com/news/security/betterleaks-a-new-open-source-secrets-scanner-to-replace-gitleaks) (not fetched).

**Dependencies**
- osv-scanner `--format` takes `table, markdown, vertical, html, json, sarif, spdx-2-3, cyclonedx-1-5`. In SARIF, "Each vulnerability (grouped by aliases) is a separate rule", one result per vulnerable package, with the location pointing at the lockfile — [docs/output.md](https://raw.githubusercontent.com/google/osv-scanner/main/docs/output.md).

### Inferences
- **Dotnetarium should be the main C# taint engine. Keep CA rules alongside it, not instead of it.** It covers SSRF and ASP.NET Core sources, which CA rules don't, and it needs no build. CA rules cover CSRF (CA3147/CA5391), cookies, deserialization settings and token-validation flags at no cost. The risk is a young project with one owner, so pin the version and keep it behind an adapter.
- **Opengrep is no longer the main C# engine.** Its weak field-taint and lack of inter-file analysis are a poor fit for DI-heavy ASP.NET Core code, where request data reaches services through fields. Use it for what Roslyn rules don't express: Jazari's own 20–30 rule pack for authorization and configuration (`[AllowAnonymous]` on mutating actions, `[IgnoreAntiforgeryToken]`, missing `[Authorize]` on a controller with POST/PUT/DELETE, `FromSqlRaw($"...")`, `AllowAnyOrigin()` together with `AllowCredentials()`, `UseDeveloperExceptionPage` outside a development check, `RequireHttpsMetadata = false`), and for TypeScript/React.
- **Secrets: stay on gitleaks 8.30.x for now.** Wrap it behind an `ISecretScanner` adapter and re-check Betterleaks once v2 ships stable Windows binaries and SARIF. Never pass `--validate` or `--verify-secrets`: offline and private.
- **NuGet audit is free when online.** `dotnet restore` already prints NU190x. Parse the restore output when the network is available, and treat NU1900 as "no data" rather than "clean". Offline, osv-scanner with its downloaded database is the source.
- DevSkim is regex-level and overlaps the others. Leave it out of the default set.
- Sonar's SSALv1 and CodeQL's terms both rule out bundling them in Jazari.

### Gaps
- No independent false-positive rate was found for Dotnetarium, CA3001-family rules, Opengrep C# rules or DevSkim. Measure all of them on the seeded-bug branch proposed in repo_review.md [T].
- Dotnetarium's speed on EduCore (631 C# files, 363k tokens) is unmeasured. LANCommander's size was not given in the doc, so the 41 s figure cannot be scaled [T].
- The exact offline behaviour of `dotnet restore` with audit (NU1900 warning or failure; any cached vulnerability data) is not stated in the docs read [T].
- Whether osv-scanner reads `obj/project.assets.json`, or only `packages.lock.json` / `deps.json`, was not confirmed here (`google.github.io` was blocked). If it needs a lockfile, Jazari must not write one into the user's repo; see design section 6.
- The latest gitleaks and Opengrep release numbers could not be re-checked (the GitHub API was blocked). repo_review.md's v8.30.1 and v1.33.1 stand.

---

## 2. Rulesets that matter for ASP.NET Core, and Opengrep C# quality vs Roslyn

### Takeaway
Map each vulnerability class to the tool that sees it semantically. Roslyn-based tools (CA rules and Dotnetarium) resolve types, attributes and overloads. Opengrep matches syntax with partial C# taint. **No scanner checks resource-level authorization (IDOR) or business rules**, so that is where the model's reading time should go.

### Cited Findings
- OWASP's .NET sinks and guards (from repo_review.md): `FromSqlRaw`, `BinaryFormatter`, `IgnoreAntiforgeryToken`, `IAuthorizationService.AuthorizeAsync`, `[AllowAnonymous]`, `LocalRedirect`/`Url.IsLocalUrl`. `[Authorize]` alone does not perform resource checks — [OWASP cheat sheet](https://cheatsheetseries.owasp.org/cheatsheets/DotNet_Security_Cheat_Sheet.html).
- CA coverage per class: SQLi CA3001/CA2100; path CA3003/CA5389; open redirect CA3007; command CA3006; XSS CA3002; CSRF CA3147/CA5391; deserialization CA2300–CA2330; JWT CA5404/CA5405; no SSRF — [security-warnings.md](https://raw.githubusercontent.com/dotnet/docs/main/docs/fundamentals/code-analysis/quality-rules/security-warnings.md).
- Dotnetarium adds SSRF, Minimal API, Razor/Blazor and SignalR sources, Dapper and Npgsql sinks, and permissive CORS with credentials — [README](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/README.md).
- Opengrep's C# limits are listed in section 1 — [wiki](https://github.com/opengrep/opengrep/wiki/Support-for-C%23). Semgrep CE taint is single-function for C# (repo_review.md).
- [M] LLMs found 0 of 19 IDORs where checks were spread across files and 0 of 18 where authorization was implicit in middleware (from repo_review.md) — [Semgrep](https://semgrep.dev/blog/2025/can-llms-detect-idors-understanding-the-boundaries-of-ai-reasoning).

### Inferences
Coverage matrix [E: tool coverage by docs, not measured]:

| Class | Primary | Secondary | Model's role |
|---|---|---|---|
| SQLi (EF `FromSqlRaw`/`ExecuteSqlRaw`, ADO, Dapper) | Dotnetarium, CA3001 | Opengrep interpolated-string rule | triage |
| Path traversal / zip-slip | Dotnetarium, CA3003, CA5389 | — | triage |
| SSRF (`HttpClient` with request URL) | Dotnetarium | Opengrep rule | triage, plus check for allow-lists |
| Open redirect | CA3007, Dotnetarium | — | triage |
| CSRF | CA3147, CA5391 | Opengrep `[IgnoreAntiforgeryToken]` rule | triage (APIs that use bearer tokens are often FPs) |
| Deserialization | CA2300–CA2330, Dotnetarium | — | triage (usually true when hit) |
| JWT and cookie flags, TLS validation | CA5404/5405, CA5382/5383, CA5359 | Dotnetarium | explain |
| CORS | Dotnetarium DNA0024 | Opengrep | explain |
| Secrets | gitleaks | Dotnetarium DNA0009/0022, CA5390/CA5403 | none (redacted, never sent to the model) |
| Vulnerable packages | osv-scanner, NuGet audit | — | none, or a short explanation |
| **Missing/insufficient authz, IDOR, tenant leaks, client-trusted amounts** | **none** (only Opengrep heuristics for a missing attribute) | Roslyn endpoint matrix (repo_review.md Stage A) | **read the code** |

- Jazari's current `Instructions` checklist asks the model to find all of these. After the pre-pass, the per-unit prompt should drop what tools already cover and focus on authorization, ownership, business rules and swallowed errors. A shorter checklist is also easier for a 3B model to follow.

### Gaps
- No head-to-head precision/recall benchmark of Opengrep C# rules against Roslyn analyzers was found.
- How Dotnetarium handles `[Authorize]` and policy-based authorization (if at all) is not documented in the pages read.

---

## 3. SARIF 2.1.0 as the common format: parsing, merging and deduping in C#

### Takeaway
Every core tool can write SARIF 2.1.0: Roslyn via `ErrorLog`, Dotnetarium `--sarif`, Opengrep `--sarif-output=`, gitleaks `-f sarif`, osv-scanner `--format sarif`. Jazari should read SARIF with a small System.Text.Json model of its own, holding only the fields it needs, rather than taking `Sarif.Sdk` (v5.8.0, which depends on Newtonsoft.Json). Dedupe on Jazari's own key, not on tool fingerprints.

### Cited Findings
- `Sarif.Sdk` is the NuGet package for the SDK and depends on Newtonsoft.Json. Its latest version is 5.8.0 — [sarif-sdk README](https://raw.githubusercontent.com/microsoft/sarif-sdk/main/README.md), [versions](https://api.nuget.org/v3-flatcontainer/sarif.sdk/index.json).
- Sarif.Multitool has `merge` (`Sarif.Multitool merge C:\Input\*.sarif --recurse true --output-directory=... --output-file=...`), `rewrite --insert TextFiles;RegionSnippets`, `match-results-forward` (labels results New, Absent or Unchanged against a baseline) and `validate`. The docs show an npm install (`npm i -g @microsoft/sarif-multitool`) and say nothing about fingerprints — [multitool-usage.md](https://github.com/microsoft/sarif-sdk/blob/main/docs/multitool-usage.md). Sarif.Multitool is also on NuGet, at 5.8.0 — [versions](https://api.nuget.org/v3-flatcontainer/sarif.multitool/index.json).
- osv-scanner SARIF uses one rule per vulnerability, with the result location set to the lockfile — [output.md](https://raw.githubusercontent.com/google/osv-scanner/main/docs/output.md).
- [S] GitHub fills in `partialFingerprints` (for example `primaryLocationLineHash`) when it is missing, and matches results across runs with it. Cross-tool dedup only works if every emitter hashes the same inputs — [GitHub SARIF docs](https://docs.github.com/ko/enterprise-server@3.3/code-security/code-scanning/integrating-with-code-scanning/sarif-support-for-code-scanning), [codelore-lib source](https://docs.rs/crate/codelore-lib/latest/source/src/output/sarif.rs).
- Dotnetarium writes data flows into SARIF and records input quality (`available`/`degraded`) and analysis limits (`within-profile`/`limited`) in it. A matching line hash "does not prove a flow" is still safe — [README](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/README.md), [ai-development.md](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/docs/ai-development.md).
- Roslyn SARIF needs `ErrorLog=<path>%2cversion=2.1`. The `%2c` is an escaped comma (from repo_review.md) — [Meziantou](https://www.meziantou.net/how-to-output-a-sarif-file-from-a-dotnet-project.htm).

### Inferences
**Minimal model (System.Text.Json, trim/AOT-safe, about 150 lines) [E]:**
```csharp
record ScanFinding(
    string Tool, string RuleId, string? Cwe, string Level,     // error|warning|note
    string Path, int StartLine, int? EndLine, string Message,
    IReadOnlyList<FlowStep> Flow,                               // from codeFlows[0].threadFlows[0].locations
    string? Snippet, bool Suppressed, string LineHash);
record FlowStep(string Path, int Line, string? Message);
```
Read `runs[].tool.driver.name`, `runs[].tool.driver.rules[]` (for CWE from `properties.tags` or `relationships`, and help text), `results[].ruleId|level|message.text|locations[0].physicalLocation.{artifactLocation.uri, region.startLine}`, `codeFlows`, and `suppressions`. Ignore everything else. This avoids Newtonsoft and the size of the full SDK in a CLI that already uses System.Text.Json (`ProjectReview.cs` uses `JsonSerializer`/`JsonDocument`).

**Normalisation before dedup:**
- Turn `file:///C:/...` URIs and `uriBaseId` into repo-relative paths with forward slashes, compared case-insensitively (Windows).
- Map rule IDs to a class bucket: `sqli|path|ssrf|redirect|csrf|deser|xss|cmd|crypto|jwt|cookie|cors|secret|dep|authz|other`. Use a static table from CA IDs, DNA IDs and Jazari's Opengrep rule IDs.
- `LineHash = sha256(squeezed line text)[..16]`. This reuses `ProjectReview.Squeeze`, so the key survives line shifts.

**Dedup key:** `(path, bucket, LineHash)`. If the line text is missing, fall back to `(path, bucket, startLine ±2)`. When findings merge, keep the highest severity, the **union of tools** ("CA3001 + DNA00xx agree"), and the longest flow. Agreement between tools is a strong precision signal, and the report should show it.

**Running Roslyn without touching the user's repo [T]:** pass global properties on the command line: `dotnet build X.sln --no-restore --disable-build-servers -p:AnalysisModeSecurity=All -p:RunAnalyzersDuringBuild=true -p:CustomAfterMicrosoftCommonTargets=%JAZARI%\jazari.targets`. The Jazari-owned `jazari.targets` sets `<ErrorLog>$(IntermediateOutputPath)jazari.sarif,version=2.1</ErrorLog>`, so each project writes its own file and they don't overwrite each other. Gather the files afterwards. This must be tested: properties defined in targets files are evaluated before `CoreCompile` runs, but how `ErrorLog` behaves in this position is not documented in the sources read.

**Not using Multitool:** Jazari only needs read, normalise, dedup, sort. `merge` would add a Node or .NET tool dependency for something Jazari's own dedup must redo anyway. `match-results-forward` is useful only if baselining across runs becomes a feature, and the JSONL store already gives this per unit.

### Gaps
- The OASIS SARIF 2.1.0 normative text for `partialFingerprints`, `codeFlows` and `suppressions` was not fetched (the oasis-tcs raw path returned 404).
- Whether Roslyn's SARIF output includes `codeFlows` for CA3001-family dataflow rules is unknown. It likely gives only the sink location [T].
- How each tool encodes CWE in SARIF (rule `properties.tags` vs `relationships`) needs checking against real output [T].

---

## 4. LLM triage of SAST findings: evidence, especially for small models, and prompt patterns

### Takeaway
New evidence since repo_review.md agrees with it. LLM triage removes many false positives with **strong** models, the gains are "limited or inconsistent" for weaker backbones, and even commercial triage is conservative about calling something a false positive. **No study tests a model around 3B.** So Granite's verdict should **sort and explain, never delete**, and each finding card should give the model small, specific evidence rather than asking it for open-ended judgement.

### Cited Findings
- [S] QASecClaw (arXiv 2605.01885) is multi-agent: a code-specialised LLM filters the candidates a SAST engine reports. It claims "88.6% (496/560) false positive suppression ratio with a true positive loss ratio of only 3.1% (40/1273)" — [arXiv](https://arxiv.org/html/2605.01885v1). The model sizes could not be read (arXiv was blocked).
- [S] Sifting the Noise (ISSTA 2026; Aider, OpenHands and SWE-agent on OWASP Benchmark and real Java with CodeQL): the FP rate fell from over 92% to as low as 6.3%, and FP identification reached up to 93.3% on real Java. "Agentic frameworks significantly outperform vanilla prompting for stronger models such as Claude Sonnet 4 and GPT-5, but yield limited or inconsistent gains for weaker backbones". Aggressive FP reduction can suppress true vulnerabilities — [ISSTA page](https://conf.researchr.org/details/issta-2026/issta-2026-research-papers/9/Sifting-the-Noise-A-Comparative-Study-of-LLM-Agents-in-Vulnerability-False-Positive-), [arXiv](https://arxiv.org/html/2601.22952v3).
- [S] "Reducing False Positives in Static Bug Detection with LLMs: An Empirical Study in Industry" (arXiv 2601.18844, January 2026) exists; its numbers could not be read — [ResearchGate](https://www.researchgate.net/publication/400117710_Reducing_False_Positives_in_Static_Bug_Detection_with_LLMs_An_Empirical_Study_in_Industry).
- [S] LLM-driven adaptive source-sink identification plus FP mitigation (arXiv 2511.04023) has the LLM propose sources and sinks for the analyzer as well as triage — [arXiv abs](https://arxiv.org/abs/2511.04023). EVIDENT (arXiv 2606.15122) has a formal analyzer, not the LLM, make the final call — [arXiv](https://arxiv.org/pdf/2606.15122).
- [V, 2nd/S] Semgrep Assistant: 96% user agreement on over 6M findings. In Semgrep's own benchmark of over 2,000 findings, agreement was 96% on true positives but only **41% on false positives**, because the Assistant was "far more conservative" in calling FPs. The docs define a separate "false positive confidence rate" of 96% (how often it is right *when* it says FP) and say this "does not mean that Assistant catches all false positives" — [96% post](https://semgrep.dev/blog/2025/building-an-appsec-ai-that-security-researchers-agree-with-96-of-the-time/), [metrics docs](https://cloudflare.semgrep.dev/docs/semgrep-assistant/metrics), [60% triage post](https://semgrep.dev/blog/2025/semgrep-is-confidently-handling-60-of-all-triage-for-users-without-reducing-coverage).
- [C] Dotnetarium's agent guide: "uncertainty is a reason to investigate, not suppress", and a filtered scan "must not be described as an unfiltered clean scan" — [ai-development.md](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/docs/ai-development.md).
- Already in repo_review.md, not repeated: ZeroFalse (flow trace, slice and CWE micro-rubric; Mixtral recall 0.135), IRIS (small models lean towards "vulnerable"), Memoir (TP recall 0.5), Datadog (prompts tuned for TPs misclassify FPs).

### Inferences
**Per-finding card for Granite (target 800–1,500 tokens) [E]:**
```
FINDING  #17  class=sqli  severity=high  tools=Dotnetarium DNA0001, CA3001 (2 agree)
RULE     "Untrusted data reaches SQL command text" (CWE-89)
FLOW     1 Controllers/ReportController.cs:42  [FromQuery] string sort       (source)
         2 Services/ReportService.cs:88        BuildOrder(sort)
         3 Services/ReportService.cs:97        db.Database.SqlQueryRaw($"... ORDER BY {sort}")  (sink)
CODE     the enclosing method of the sink, numbered lines (cap 60 lines),
         plus +/-5 lines around the source line
FACTS    (from Roslyn index, not the model) endpoint=GET /reports [Authorize(Policy="Staff")];
         sort has no allow-list in BuildOrder: no Contains/switch on sort found
QUESTIONS answer each yes/no/unknown with a quoted line:
  Q1 Does the value at the sink still come from the request? 
  Q2 Is there an allow-list, parameterisation or type restriction between source and sink?
  Q3 Is the endpoint reachable by a non-admin user?
```
Output under a grammar-enforced schema:
`{"q":[{"id":"Q1","a":"yes|no|unknown","quote":"..."}...],"verdict":"likely_real|likely_false|unsure","why":"<=200 chars","fix":"<=300 chars"}`.

Design rules that follow from the evidence:
1. **Fixed 2–4 yes/no questions per class** (ZeroFalse's micro-rubric, simplified for a 3B model). Use one question set per bucket: SQLi, path, SSRF and so on.
2. **Every "no" answer needs a quote**, checked by the existing `Check`/`Squeeze` logic. An answer whose quote isn't found becomes `unknown`.
3. **Jazari computes the verdict from the answers** (for example, any `Q2=yes` with a verified quote means `likely_false`). The model's free-text `verdict` is advisory only. This uses the model as an evidence extractor, which suits small models better than open judgement, given the IRIS and Sifting-the-Noise results.
4. **Never drop a finding.** `likely_false` moves it to a "Probably not exploitable, model's reasons" section with the quote, as recommended in repo_review.md and Dotnetarium's guide.
5. **Put the fixed prefix first** (system text plus the class question set) and group cards by class. This lets llama-server's prompt cache reuse the prefix across cards (the cache itself is still untested, see repo_review.md).
6. Facts such as `[Authorize]`, endpoint route and allow-list presence come **from Roslyn, not the model**. Semgrep's IDOR study shows the model cannot see across files.

**Should the small model judge at all?** Run an A/B test on the seeded branch [T]: (a) tools only, sorted by tool agreement and severity; (b) the same plus Granite question-answers. If (b) does not improve the ranking of seeded true bugs (for example mean rank, or how many seeded bugs are in the top 20), ship the model as **explainer only**: write `why` and `fix` text for each finding and skip verdicts.

### Gaps
- No primary-source numbers for any model under 7B on SAST triage. The smallest sizes reported in ZeroFalse and QASecClaw could not be read (arXiv blocked).
- No published triage study on C#/.NET findings. All strong results are Java (CodeQL, OWASP Benchmark) or C/C++.
- The precision gain from tool-agreement signals (two or more tools on the same line) is not measured in any source found; it is a hypothesis to test.

---

## 5. Picking the remaining files for the model (riskiest first) and estimated time saved

### Takeaway
After the pre-pass, the model's reading budget should go **where scanners are blind**: entry-point and service code that handles authorization, ownership and money, especially files with **no** scanner hits. Files with hits get short triage cards rather than a full read. On EduCore-sized code (363k tokens) this gives a first useful report in **about 1 hour instead of 3.4–7 hours** [E]. Most of the gain comes from capping and grouping triage cards, since answer speed (6.2 tok/s) dominates per-card cost.

### Cited Findings
- Granite 4.1 3B Q4_K_M on this laptop reads at 28.8 tok/s and answers at 6.2 tok/s [M] — from repo_review.md (docs/OPTIMIZATION-LOG.md).
- EduCore hand-written C# is about 363k tokens (3.4 h of reading). The hot-path names alone come to 25k tokens (about 14 min) [M] — repo_review.md.
- [M, by the tool author] Dotnetarium scans a 21-compilation solution in about 41 s — [performance-budget-matrix.md](https://raw.githubusercontent.com/dotnetarium/dotnetarium/main/docs/performance-budget-matrix.md).
- [M] VulnGym: most hard failures come from never reaching the right file (repo_review.md), so ranking affects recall as well as speed.
- [M] LLM IDOR misses are cross-file (0/19, 0/18) — [Semgrep](https://semgrep.dev/blog/2025/can-llms-detect-idors-understanding-the-boundaries-of-ai-reasoning).

### Inferences
**How scanner output changes ranking.** Extend repo_review.md's score; the new terms are marked ★:
```
score = 10*is_entrypoint + 8*has_sink + 5*name_match + 4*reachable_depth<=3 + 2*churn - 8*test_file
      ★ + 6*authz_relevant (controller/handler with mutating verbs, or service taking an id/tenant/amount from a DTO)
      ★ + 4*no_scanner_hits_but_entrypoint      // scanners are blind to authz, so read it
      ★ - 6*fully_covered_by_cards               // every risky line of the file already went to the model as a card
      ★ + 3*on_a_flow_path_of_unconfirmed_card   // card answered "unknown": read the whole file next
```
- Files that only hold tool findings and no entry-point logic (helpers, crypto utilities) are covered by their cards and are **not re-read** in full.
- **Card answer `unknown` pulls the file forward** into the full-read queue. This is the adaptive "escalate when uncertain" step.
- Files with zero hits and no entry-point role (DTOs, entities, mapping) are read last or summarised from signatures (repo_review.md).
- The per-unit `Instructions` for the full read becomes an authorization and business-logic checklist (section 2). Add `scannerFacts` to the context so the model does not re-report known tool findings. This needs a `PromptVersion` bump. Add a hash of the scanner facts to `Key()` so units re-run when tool findings change.

**Time model [E], EduCore-sized, assuming the speeds above:**

| Stage | Volume | Time |
|---|---|---|
| Scanners (Roslyn build, Dotnetarium no-build, Opengrep, gitleaks, osv) | whole repo | ~3–10 min [E; build time and Dotnetarium on EduCore T] |
| Dedup and grouping | — | seconds |
| Triage cards | per card ~1,200 read tokens (42 s) + ~150 answer tokens (24 s) ≈ **66 s** | 40 cards ≈ 45 min; 150 cards ≈ 2.75 h |
| Hot-path/authz full reads | ~25k tokens + answers | ~20–30 min |
| Remaining ranked code | ~300k tokens | ~3 h, optional, overnight |

- **Capping and grouping matters most.** A raw CA plus Dotnetarium run on a real app could give hundreds of findings. Grouping is needed: one card per `(bucket, enclosing method)`, cards for the low-value classes (cookie flags, weak random in non-security code) only sent to the model on request, and `note`-level findings left out. A cap of about 40 cards (high first) keeps triage under an hour [E]. Deterministic findings (secrets, packages, deserialization settings) need no model at all: they appear in the report straight from the scanner.
- **Time to a useful report: about 1 h** (scanners + 40 cards + hot paths), against 3.4 h of reading alone today and the observed 3–7 h. **Full coverage is still possible** by letting the ranked full read continue overnight. Stopping early still leaves the highest-value parts done, which the existing resumable JSONL loop already supports.
- **Answer budget:** the current `AnswerTokens = 500` per unit costs up to 80 s at 6.2 tok/s. Triage cards should cap answers at about 150–200 tokens through the schema's `maxLength` and short question answers.

### Gaps
- The real finding count on EduCore after dedup and grouping is unknown, and it is the main driver of triage time [T].
- Prompt-prefix caching in llama-server is untested here. With it, the 42 s per card might fall to about 25 s [E].
- No evidence for the ★ weights; tune them on the seeded-bug branch by measuring seeded bugs found per hour.

---

## 6. Objective: implementable design for the scanner pre-pass

### Takeaway
Add a `ScanPrepass` stage in front of `ProjectReview`. It runs pinned, locally installed tools as child processes (no Docker, no network), reads their SARIF into one deduped `ScanFinding` list, writes deterministic sections of the report straight away, sends grouped findings to Granite as question cards, and passes a re-ranked unit list to the existing full-read loop with a narrower checklist.

### Cited Findings
- All commands, flags and licences are cited in sections 1–3. The existing resumable loop and quote gate are in `src/MyOwnLLM.Cli/ProjectReview.cs` (read 2026-11-02).

### Inferences
**Tool manifest** (`%LOCALAPPDATA%\Jazari\tools\manifest.json`, with versions pinned and SHA-256 checked) [E]:

| Tool | Get it | Run (cwd = repo; outputs under `%LOCALAPPDATA%\Jazari\review\<repohash>\sarif\`) | Offline |
|---|---|---|---|
| Roslyn CA | in .NET SDK | `dotnet build <sln> --no-restore --disable-build-servers -p:AnalysisModeSecurity=All -p:CustomAfterMicrosoftCommonTargets=<jazari.targets>` [T] | yes, after the user's own restore |
| Dotnetarium 2.8.x | `dotnet tool install --tool-path <tools> dotnetarium --version 2.8.1` (local, not global) | `dotnetarium <sln> --sarif dn.sarif` (no-build default; never `--verify-secrets`) | yes |
| Opengrep | exe zip with DLLs, unpacked to tools dir | `opengrep scan --sarif-output=og.sarif -f <jazari-rules> --taint-intrafile .` [T] | yes, local rules only |
| gitleaks 8.30.x | Windows zip | `gitleaks dir --redact -f sarif -r gl.sarif .` (+ `gitleaks git` for history) | yes |
| osv-scanner | exe | `osv-scanner scan --offline --format sarif ... > osv.sarif` with a DB refreshed on demand by the user | yes, after one DB download |
| NuGet audit | in SDK | parse NU1901–NU1904 from the user's normal restore output; NU1900 means "no data" | online only |

- **No writes to the user's repo:** outputs, `jazari.targets` and Jazari's rule pack live outside it. If osv-scanner needs a lockfile, generate it in a temp copy or use `deps.json` from `bin` [T]. Dotnetarium's `.dotnetarium/` suppression folder should not be created, because Jazari does not pass `--suppress`.
- **Install path:** tools are downloaded once when online (`jazari tools install`). They are verified by hash, and `/review` lists missing tools and continues without them. Coverage per tool goes at the top of the report ("Dotnetarium: not installed → SSRF not checked").
- **Process hygiene:** run tools **before** loading the model (16 GB RAM, repo_review.md), in sequence, with per-tool timeouts. Record exit codes, but treat Dotnetarium exit 2 and Roslyn compile errors as degraded coverage, not failure.

**Flow inside `/review`:**
```
0 Inventory (existing ProjectFiles.Load)
1 ScanPrepass.Run -> sarif/*.sarif          (minutes, no model)
2 SarifReader -> List<ScanFinding> -> Normalize -> Dedup(path,bucket,LineHash) -> Group(bucket, enclosing method)
3 Report.WriteDeterministic: secrets (redacted), packages, tool-agreeing high findings  <- first output, minutes in
4 Triage: top-N groups -> card (flow + enclosing method + Roslyn facts + class questions) -> grammar JSON
         -> answers quote-checked -> Jazari computes verdict -> results.jsonl (key = hash(card text + PromptVersion))
5 Rank units with scanner-aware score; skip units fully covered by cards
6 Existing ProjectReview loop with an authz/business-logic checklist + scannerFacts in context
7 Report: coverage, tool-agreeing findings, triaged findings (likely real / unsure / probably not), model-only findings, rejected
```

**Code changes in `ProjectReview.cs` terms [E]:**
- New `ScanFinding`, `SarifReader`, `ScanPrepass` and `TriageCard` types. `Finding` gets `Source` (`tool:<names>` or `model`) and `Verdict`.
- `Units(...)` takes a ranking function instead of relying only on `ProjectFiles.Risk`.
- `Key(...)` includes a hash of the scanner facts sent with the unit. Bump `PromptVersion` to "4".
- `Instructions` is split into a triage prompt (per class) and an authz/business prompt (full read). Each keeps the "quote one whole line" rule that `Check` enforces.

### Gaps
- Every command marked [T] needs one dry run on the Windows laptop against EduCore before the design is built. Above all: the per-project `ErrorLog` via `CustomAfterMicrosoftCommonTargets`, Opengrep's exact flags, osv-scanner's NuGet inputs, and Dotnetarium's no-build success rate.
- Whether the small model improves ranking over tools alone is the central open question (section 4). Plan the A/B test before investing in the triage step.
