# Windows 11 OS/Power/Thermal Tuning for Sustained Local LLM Inference on a 15 W Kaby Lake R Laptop (i5/i7-8250U/8550U, 16 GB DDR4-2400, MX150)

Research date: 2026-10-29. Most hardware-specific data on Kaby Lake R (launched 2017) dates from 2017-2020 and is flagged as older. The Microsoft docs cited were updated between 2024 and 2026. Budget-limited pass: about 15 tool calls. Forum sources (TechPowerUp) are anecdotal. Some pages could not be fetched (the TechPowerUp ThrottleStop guide returned 403).

## 1. Power plans / power mode, processor max state, turbo, EcoQoS / Efficiency mode

### Takeaway
LLM token generation on this machine is limited by memory bandwidth, so turbo clocks buy little decode speed but cost a lot of heat. Capping turbo is a cheap, reversible test that probably improves sustained tokens/sec per watt, through "Maximum processor state" 99% or "Processor performance boost mode = Disabled". Benchmark it. Use Best performance (or Balanced) on AC rather than Battery saver. Don't put the inference process in Efficiency mode unless you want it slow: EcoQoS *always* picks the most efficient frequency, even on AC.

### Cited Findings
- Each generated token streams the full weight set through memory with almost no reuse between tokens, so generation is memory-bandwidth-bound, not compute-bound. Hyper-threads add contention, not bandwidth, and generation saturates near the physical core count — [InventiveHQ](https://inventivehq.com/blog/cpu-thread-scaling-llm-inference); see also the llama.cpp issue ["llama.cpp is memory bound"](https://github.com/ggml-org/llama.cpp/issues/1437)
- llama.cpp docs: `--threads` defaults to -1. For best performance, match the number of *physical* cores, not logical ones — [llama.cpp completion README](https://github.com/ggml-org/llama.cpp/blob/master/tools/completion/README.md)
- llama.cpp's token-generation tips suggest lowering the thread count (even to 1) to check whether oversubscription is the problem — [llama.cpp token_generation_performance_tips (mirror)](https://git.peisongxiao.com/CS348Project/llama.cpp/src/branch/master/docs/development/token_generation_performance_tips.md)
- Setting "Maximum processor state" to 99% often disables Turbo Boost, so the CPU runs at base clock under peak load. One user reported a CPU drop from 85-90 °C to the 60s, at the cost of about 10 fps in games — [Mirazon](https://www.mirazon.com/laptop-overheating-due-turbo-boost-heres-can/); [Geeks3D](https://www.geeks3d.com/20170213/how-to)
- Caveat: several sources say the 99% trick does not work reliably on newer Windows builds and hardware, and suggest a BIOS option or ThrottleStop instead. The clock response is non-linear (on an i5-2410M, 99% gave 2.3 GHz and 76-98% gave about 1.8 GHz) — [Geeks3D](https://www.geeks3d.com/20170213/how-to); [MakeTechEasier](https://maketecheasier.com/?p=851395)
- Windows exposes PerfBoostMode, MaxFrequency, MaxPerformance, MinPerformance and PerfEnergyPreference (EPP) as processor power settings, per profile and per QoS level. Microsoft notes "PPM profiles are tuned by Silicon vendors" — [MS Learn: Processor power management options](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configure-processor-power-management-options)
- QoS levels (MS Learn, updated 2025-12):
  - **Low** QoS (minimized or occluded windows) and **Utility** QoS (background services) select the most efficient frequency *only on battery*.
  - **Eco** QoS "Always selects most efficient CPU frequency".
  - A process running at reduced thread priority can heuristically get a lower QoS.
  - Windows (Ge 2025.05+) may lower the foreground app to Medium QoS after user inactivity *on battery*. The registry value `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling\DisableUserPresenceQos=1` disables this.
  
  Source: [MS Learn: Quality of Service](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service)
- Microsoft says QoS can also "reduce CPU power consumption while operating on AC power to reduce thermal output which can lead to high fan noise, or even thermal throttling" — [MS Learn: Quality of Service](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service)
- Task Manager's Efficiency mode = lower base priority + EcoQoS. The Best performance power mode turns off power throttling. Per-app throttling can be disabled in Settings > Battery usage per app — [Windows Latest](https://windowslatest.com/2022/02/22/interesting-details-of-windows-11s-performance-feature-efficiency-mode-revealed); [TechRepublic](https://www.techrepublic.com/article/ecoqos-gives-windows-11-apps-better-battery-life/)
- Developers opt a process in or out of EcoQoS through `SetProcessInformation` with `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` — [MS Learn: Quality of Service](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service)

### Inferences
- Kaby Lake R is not a hybrid CPU (no E-cores), so on this chip EcoQoS only lowers frequency/EPP; there is no core steering. On AC, a llama.cpp server in a minimized console is probably *not* throttled (Low QoS throttles only on battery). On battery, it would be throttled unless excluded.
- Disabling turbo (base 1.6 GHz on the 8250U, 1.8 GHz on the 8550U) will hurt prompt processing (prefill is compute-bound) much more than decode. For chat-style use with short prompts, decode dominates, so the expected net effect is roughly flat tokens/sec with much lower package power and temperature. I found no published measurement of tokens/sec with turbo on vs off on this CPU, so benchmark it with `llama-bench` (pp512/tg128) both ways.
- Intermediate option: rather than fully disabling turbo, cap it with "Maximum processor frequency" (PROCFREQMAX, MHz, e.g. 2200-2600) or with ThrottleStop's turbo ratio / EPP settings.
- Suggested power-plan checklist (AC):
  - Power mode Balanced or Best performance.
  - Min processor state 5%.
  - Max processor state 99% (or boost mode Disabled / Efficient Enabled) as a heat-saving experiment.
  - Set `DisableUserPresenceQos=1` only if you run long unattended jobs on battery.

### Gaps
- I found no measured tokens/sec-per-watt data for turbo on vs off on 8th-gen U-series CPUs.
- I did not verify current Windows 11 (25H2/26H1) behaviour of the 99% trick on Kaby Lake R with Speed Shift (HWP). Some sources say it can be ignored when HWP is active.

## 2. ThrottleStop / Intel XTU on 8th-gen U: PL1/PL2, undervolt (Plundervolt locks), Speed Shift EPP, risks

### Takeaway
Undervolting (about -75 mV core/cache was typical for the 8550U) was the single biggest heat reducer on Kaby Lake R. Most OEM BIOS updates after December 2019 lock it because of the Plundervolt mitigation (INTEL-SA-00289 / CVE-2019-11157). If ThrottleStop's FIVR shows "Locked", the only "fixes" are BIOS modding or downgrading, which carry brick/security risk and are not recommended. Lowering PL1/PL2 and setting EPP higher (more efficient) are safe and still work on locked machines.

### Cited Findings
- Intel's Plundervolt fix was a microcode + BIOS update that locks voltage to default settings. It affects 6th-10th gen Core CPUs, and Intel advises installing the latest OEM BIOS. Reported to Intel in June 2019, disclosed in December 2019 — [SecurityWeek](https://www.securityweek.com/plundervolt-attack-uses-voltage-steal-data-intel-chips/); [The Hacker News](https://thehackernews.com/2019/12/intel-sgx-voltage-attack.html)
- ThrottleStop author (unclewebb): when the FIVR window says Locked, the BIOS has locked out CPU voltage control, and the BIOS has to be modified to remove the lock bit. Installing an older BIOS does not always remove the lock — [TechPowerUp forum](https://techpowerup.com/forums/goto/post?id=5058962); [TechPowerUp forum](https://techpowerup.com/forums/goto/post?id=5057728)
- Windows virtualization features (Hyper-V / hypervisor platform, and by extension VBS/Memory Integrity) can block undervolt writes on 8250U systems. "Locked" can also refer to the PowerCut feature, not voltage — [TechPowerUp: undervolting not working](https://www.techpowerup.com/forums/threads/undervolting-not-working.264318)
- BIOS modding risk: Intel Boot Guard may brick a system that is flashed with a modified image — [bios-mods.com forum](https://www.bios-mods.com/forum/Thread-REQUEST-ASUS-G712LWS-BIOS-Unlock?pid=183853)
- 8550U undervolt: about -75 mV on both core and cache is usually stable (unclewebb). One user was unstable at -100 mV. iGPU undervolting gives little benefit when there is an NVIDIA dGPU — [TechPowerUp: ThrottleStoping a 8550U](https://techpowerup.com/forums/threads/throttlestoping-a-8550u-some-questions.277011)
- Kaby Lake R power limits (one user, ThinkPad X1 Carbon, older data): ThrottleStop power-limit settings may not take effect, and XTU worked better. Plugging in the charger or changing Windows power settings can reset limits — [Medium: 45W performance from 15W Kaby Lake-R](https://medium.com/@n4ru/45w-performance-from-15w-kaby-lake-r-d8d5e7ea4fad)
- Many laptops set the long-term power limit (PL1) at the 15 W TDP, and ThrottleStop cannot exceed an internally set limit on locked machines (forum, anecdotal) — [TechPowerUp forum](https://techpowerup.com/forums/goto/post?id=4288080)
- Notebookcheck: i5-8250U cTDP is 7.5-25 W depending on design. In a Cinebench R15 multi loop, results by machine were:
  - XPS 13 9370: about 540-630 points.
  - XPS 13 9360: drops to about 520.
  - HP ProBook 470 G5: about 570.
  
  Sustained-load throttling is usually thermal, though some laptops are power-limited (older data, 2017-2018) — [Notebookcheck XPS 13 9370 (DE)](https://www.notebookcheck.com/Das-XPS-13-9370-holt-mehr-Leistung-aus-dem-i5-8250U-heraus.279976.0.html); [Notebookcheck i5-8250U](https://www.notebookcheck.net/Intel-Core-i5-8250U-SoC-Benchmarks-and-Specs.242172.0.html); [Notebookcheck: throttling in reviews](https://www.notebookcheck.net/Opinion-It-s-time-we-talked-about-throttling-in-reviews.234232.0.html)

### Inferences
- For a *cooler* sustained load, the goal is the opposite of most ThrottleStop guides (which raise PL1 to 25-45 W). Set PL1 about 10-12 W and PL2 about 15-20 W (or a short turbo time). With EPP at 128-192 (balanced/efficient) or a turbo ratio cap, the chip settles at a stable clock instead of oscillating against PROCHOT. I did not find a source quantifying this on Kaby Lake R; measure with HWiNFO + llama-bench.
- Leave BD PROCHOT and thermal protections on. Never disable them on a thin chassis.
- Safety ranking, safe to risky:
  1. Power plan.
  2. PL1/PL2/EPP in ThrottleStop or XTU (reverts on reboot, no hardware risk).
  3. Undervolt where unlocked (crash risk only, test with stress tests).
  4. BIOS downgrade or modding (brick risk, removes a security fix). Not recommended.
- If undervolting is unlocked, enabling it may require disabling VBS/Memory Integrity. That is a security trade-off: flag it to the user rather than recommend it by default.

### Gaps
- I couldn't fetch the canonical TechPowerUp ThrottleStop Guide (403). I found no sourced EPP numeric recommendations specific to Kaby Lake R.
- No per-OEM list of which 8th-gen U laptops still allow undervolting on the latest BIOS.
- The status of Intel XTU support for 8th-gen mobile in 2026 is unverified (XTU has dropped older platforms in some releases).

## 3. Process priority and core affinity for a background inference process

### Takeaway
Run the server with physical-core threads (`-t 4`) or one fewer (`-t 3`) to leave a core for the IDE and browser. llama.cpp has built-in `--prio`, `--cpu-mask`/`--cpu-range` and `--poll` controls. Low or below-normal priority keeps the foreground responsive, but a reduced priority can heuristically lower the process's QoS and slow it on battery.

### Cited Findings
- llama.cpp exposes these controls — [llama.cpp completion README](https://github.com/ggml-org/llama.cpp/blob/master/tools/completion/README.md):
  - `--prio` (low -1, normal 0, medium 1, high 2, realtime 3; default 0).
  - `--cpu-mask` / `--cpu-range` for affinity.
  - `--poll` (0 = no polling, default 50).
  - `--threads` matching physical cores.
- Windows assigns QoS heuristically, partly from thread priority: "threads running with reduced thread priority can imply a lower QoS level" — [MS Learn: Quality of Service](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service)
- Generation saturates near the physical core count, and hyper-threads don't add bandwidth — [InventiveHQ](https://inventivehq.com/blog/cpu-thread-scaling-llm-inference)

### Inferences
- On a 4C/8T chip, `-t 4` is the expected sweet spot. `-t 3` probably loses little decode speed, because bandwidth is the limit, and frees a core for UI work.
- Lowering `--poll` (e.g. 0) should reduce busy-wait spinning between tokens, which cuts idle heat and frees CPU for the foreground at a small latency cost.
- Avoid `--prio 2/3` (high/realtime) on a 4-core laptop: it can starve UI threads.
- The MX150 (2 GB VRAM, about 25 W variants or a 10 W Max-Q) shares the heatpipe with the CPU on many thin designs. Partial GPU offload (`-ngl`) adds heat to the same cooling budget. This is an inference: verify per model.

### Gaps
- No measured data on foreground responsiveness vs `-t`/`--prio` settings on 4C/8T laptops.

## 4. Memory: pagefile, avoiding swap, mmap vs mlock, large pages

### Takeaway
With a 2-3 GB GGUF, 16 GB is enough. Keep a system-managed pagefile (don't disable it). Keep mmap (the default). Use `--mlock` / load-mode `mlock` only if Windows memory compression or paging evicts weights while the IDE and browser are busy. Large pages need SeLockMemoryPrivilege granted through Group Policy and contiguous RAM. They are hard to get on a long-running desktop and are not a practical win here.

### Cited Findings
- llama.cpp `--load-mode` (default auto) memory-maps the model. "mlock: force system to keep model in RAM rather than swapping or compressing." Disabling mmap gives a slower load but may reduce pageouts if mlock isn't used. mlock needs more RAM — [llama.cpp completion README](https://github.com/ggml-org/llama.cpp/blob/master/tools/completion/README.md)
- On Windows, llama.cpp implements mlock with `VirtualLock`. It retries after growing the process working set, and otherwise prints a warning. VirtualLock fails if the size exceeds the minimum working set (older code snapshot) — [llama.cpp commit mirror](https://app.semanticdiff.com/gh/ggml-org/llama.cpp/commit/563cdc391dde140f1084d1012234e8e6f57f881f)
- Large pages need SeLockMemoryPrivilege plus `MEM_LARGE_PAGES` in VirtualAlloc. They must be reserved and committed in one operation. They "may be difficult to obtain after the system has been running for a long time" because they need contiguous physical memory — [MS Learn: Large-Page Support](https://learn.microsoft.com/en-us/windows/win32/memory/large-page-support)
- The privilege is disabled for all users by default and must be granted (typically through the Group Policy Editor). Without it, VirtualAlloc fails with error 1314 — [WineHQ bug quoting y-cruncher guide](https://list.winehq.org/hyperkitty/list/wine-bugs@list.winehq.org/thread/LADAYLKMLEYKN6AA2IPB47KVROT6ZRSW/); [OSR community](https://community.osr.com/discussion/comment/290432/)
- mmap vs no-mmap load time (Linux DGX Spark, not Windows): 1 min 44 s with mmap vs 56 s with `--no-mmap` for gpt-oss-120b — [NVIDIA forum](https://forums.developer.nvidia.com/t/very-slow-mmap-on-dgx-spark-that-affects-model-loading-questions-to-nvidia/349886)

### Inferences
- Budget for 16 GB: OS about 3-4 GB, browser 2-4 GB, IDE (VS Code + language servers) 1.5-3 GB, model 2-3 GB, KV cache 0.2-1 GB. That is roughly 9-15 GB, which is tight but workable. Disabling the pagefile risks commit-limit crashes. Leave it system-managed on SSD.
- mmap'd weights are file-backed, so under pressure Windows can drop them and re-read them from SSD rather than writing them to the pagefile. Each re-fault stalls decode. Use `--mlock` if you see token-rate dips when switching apps. Check the console for a VirtualLock warning.
- llama.cpp does not appear to request large pages for model weights on Windows (I found no source showing it does). Granting SeLockMemoryPrivilege mainly helps VirtualLock-based mlock semantics, not huge-page TLB gains.

### Gaps
- I found no Windows-specific measurements of mmap vs no-mmap vs mlock token rate or load time for llama.cpp.
- I could not confirm whether current llama.cpp on Windows uses SeLockMemoryPrivilege or large pages at all.

## 5. Cooling: pads, cleaning/repasting, fan control

### Takeaway
Raising the rear of the laptop alone gave about 7 °C. A pad with fans that blow into the bottom intakes gave about 14.5 °C in one controlled (older) test. Pads do nothing for laptops with closed-off bottom vents. Cleaning dust and repasting a 6-8-year-old 2017-2019 laptop is plausibly the largest single gain, but I found no sourced measurement for ultrabooks.

### Cited Findings
- Overclockers, Cooler Master NotePal ErgoStand on a Dell Studio 1537 (older data): elevation alone gave -7.0 °C (8%), and the pad with fans gave -14.5 °C (16%) vs flat — [Overclockers](https://www.overclockers.com/cooler-master-notepal-ergostand/)
- Vortez group test: about 4-5 °C drop at idle for all pads, and weaker pads struggled under load — [Vortez](https://www.vortez.net/articles_pages/cooler_master_notebook_cooling_pad_group_test,10.html)
- Pads only help when their airflow reaches the intakes. The Asus TUF A15, with closed-off bottom vents, saw no benefit. Suggested method: log temperature, clock and power for 30 minutes with and without the pad at the same ambient — [Evetech](https://evezone.evetech.co.za/quick-bytes/laptop-cooling-pad-worth-it-hot-gaming-laptop); [TechEnclave](https://www.techenclave.com/goto/post?id=2485603)
- Vendor and listicle claims of 15-44 °C drops lack methodology. Treat them as marketing — [ShopSavvy](https://shopsavvy.com/best-picks/best-laptop-cooling-pads)

### Inferences
- For a 15 W chip that throttles thermally, a 5-10 °C lower die temperature typically means it holds PL1 clocks instead of dropping clocks. Expect more stable, not dramatically higher, token rates.
- Fan-control tools are OEM-specific (e.g. Dell: i8kutils/Dell Power Manager thermal modes, Lenovo: Vantage/TPFanControl, HP: OMEN/none). Use the OEM "cool"/"performance" thermal profile first.

### Gaps
- No sourced before/after repaste numbers for 8th-gen U-series ultrabooks.
- No 2025-2026 fan-control tool compatibility list for specific models (the model is unknown).

## 6. Background noise: Search indexing, Defender, other services

### Takeaway
Defender real-time scanning mainly hits when the model file (multi-GB) is opened or loaded, not during steady inference. A narrow folder or process exclusion, or putting models on a trusted Dev Drive (Defender performance mode, asynchronous scans), cuts load-time cost. Microsoft positions performance mode as safer than folder exclusions. Pause Search indexing for the models folder. Other background tasks matter mostly through memory and disk contention, not memory bandwidth.

### Cited Findings
- Defender performance mode (Windows 11, Dev Drive only, real-time protection must be on) defers scans until after file open ("Open now, scan later"). Microsoft says it gives "significantly better protection than other performance tuning methods, such as using folder exclusions". Requires platform 4.18.2303.8+. Enable with `Set-MpPreference -PerformanceModeStatus Enabled` (doc updated 2026-09) — [MS Learn: Defender performance mode](https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-endpoint-antivirus-performance-mode)
- Dev Drive requirements: ReFS, minimum 50 GB, Windows 11 build 22621.2338+, 16 GB RAM recommended. It cannot be C:. It can be a VHDX. Trust it with `fsutil devdrv trust D:`. ReFS uses slightly more memory than NTFS — [MS Learn: Dev Drive](https://learn.microsoft.com/en-us/windows/dev-drive/)
- For MsMpEng high-CPU troubleshooting, Microsoft recommends using the Defender Performance Analyzer to find hot paths, then adding exclusions — [MS Learn: Defender performance mode](https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-endpoint-antivirus-performance-mode)
- Process exclusions are sometimes more effective than folder exclusions: one user's MsMpEng dropped to 0% CPU after excluding processes. Short spikes are normal and don't warrant exclusions; use narrow exclusions for trusted folders only — [RWS community](https://community.rws.com/product-groups/trados-portfolio/trados-studio/f/studio/10892/windows-defender-exclusions-for-studio); [Microsoft Q&A](https://learn.microsoft.com/en-au/answers/questions/1145419/high-cpu-usage-of-windows-defender-(antimalware-se)
- Energy Star X (open source) applies EcoQoS to background apps but not the foreground app. It can throttle browser and background clutter — [GitHub EnergyStarX](https://github.com/JasonWei512/EnergyStarX/blob/develop/README.md)

### Inferences
- Safe checklist:
  1. Exclude the models folder (e.g. `C:\MyOwnLLM\models`) from Search indexing via Indexing Options.
  2. Add a Defender *path* exclusion for `*.gguf` in that folder only. Model files from untrusted sources are a risk: download only from known HF repos. Or use a Dev Drive.
  3. Schedule Defender full scans and Windows Update active hours outside inference sessions.
  4. Close Chromium tabs, or let Edge/Chrome sleeping tabs or efficiency mode run.
- A process exclusion for llama-server.exe is riskier (any file it opens is unscanned). Prefer path or extension exclusions.
- Don't disable Defender real-time protection entirely.

### Gaps
- No measurements of how much Search indexing, SysMain or telemetry reduce LLM token rate on Windows 11.
- No source quantifies Defender's impact on GGUF load time specifically.
